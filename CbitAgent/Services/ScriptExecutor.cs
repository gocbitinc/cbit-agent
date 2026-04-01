using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CbitAgent.Configuration;
using CbitAgent.Models;
using Microsoft.Extensions.Logging;

namespace CbitAgent.Services;

public class ScriptExecutor
{
    private readonly ApiClient _apiClient;
    private readonly ConfigManager _configManager;
    private readonly ILogger<ScriptExecutor> _logger;
    private static readonly HttpClient SharedHttpClient = new() { Timeout = TimeSpan.FromMinutes(5) };

    private const int MaxTimeoutSeconds = 3600; // 1 hour max

    public ScriptExecutor(ApiClient apiClient, ConfigManager configManager, ILogger<ScriptExecutor> logger)
    {
        _apiClient = apiClient;
        _configManager = configManager;
        _logger = logger;
    }

    public async Task ExecuteAsync(PendingScript script)
    {
        var executionId = script.ExecutionId;

        // Validate execution_id — prevent path traversal
        if (string.IsNullOrEmpty(executionId) ||
            executionId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            executionId.Contains(".."))
        {
            _logger.LogError("Invalid execution_id format: rejected for path safety");
            return;
        }

        // Verify RSA-PSS SHA-256 signature before any execution (covers full payload)
        var signingPublicKey = _configManager.Config.SigningPublicKey;
        if (!VerifyScriptSignature(script, signingPublicKey))
        {
            _logger.LogError("Script execution {ExecutionId}: signature verification failed, aborting", executionId);
            await ReportResultAsync(executionId, new ScriptResult
            {
                Status = "error", ExitCode = -1, Stdout = "",
                Stderr = "Script signature verification failed",
                StartedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow
            });
            return;
        }

        // Validate script content is not empty
        if (string.IsNullOrWhiteSpace(script.ScriptContent))
        {
            _logger.LogError("Script execution {ExecutionId}: empty script content, aborting", executionId);
            await ReportResultAsync(executionId, new ScriptResult
            {
                Status = "error", ExitCode = -1, Stdout = "",
                Stderr = "Empty script content",
                StartedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow
            });
            return;
        }

        // Cap timeout to prevent abuse (max 1 hour)
        var timeoutSeconds = Math.Min(script.TimeoutSeconds, MaxTimeoutSeconds);
        if (timeoutSeconds <= 0) timeoutSeconds = 300; // default 5 minutes

        var workDir = Path.Combine(Path.GetTempPath(), $"axis-script-{executionId}");

        _logger.LogInformation("Starting script execution {ExecutionId} in {WorkDir}", executionId, workDir);

        try
        {
            Directory.CreateDirectory(workDir);

            // Download required files
            if (script.Files != null && script.Files.Count > 0)
            {
                foreach (var file in script.Files)
                {
                    // H1: Validate filename — prevent path traversal and dangerous names.
                    // Path.GetFileName strips any directory prefix; if the result differs
                    // from the original, the name contained path separators — reject it.
                    var safeFilename = ValidateHelperFilename(file.Filename, executionId);
                    if (safeFilename == null)
                    {
                        await ReportResultAsync(executionId, new ScriptResult
                        {
                            Status = "error", ExitCode = -1, Stdout = "",
                            Stderr = "Helper file rejected: invalid filename",
                            StartedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow
                        });
                        return;
                    }

                    // H2: Require SHA-256 content hash in the signed payload.
                    // The hash is part of the RSA-PSS-signed canonical payload, so a missing
                    // or mismatched hash means the file content was not committed to by the signer.
                    if (string.IsNullOrEmpty(file.FileHash))
                    {
                        _logger.LogError(
                            "Script execution {ExecutionId}: helper file '{Filename}' has no hash in signed payload — rejecting",
                            executionId, safeFilename);
                        await ReportResultAsync(executionId, new ScriptResult
                        {
                            Status = "error", ExitCode = -1, Stdout = "",
                            Stderr = "Helper file hash not present in signed payload — rejecting",
                            StartedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow
                        });
                        return;
                    }

                    var filePath = Path.Combine(workDir, safeFilename);
                    _logger.LogInformation("Downloading file {Filename} for execution {ExecutionId}",
                        safeFilename, executionId);
                    await DownloadAndVerifyFileAsync(file.DownloadUrl, filePath, file.FileHash, safeFilename);
                }
            }

            // Inject variables into script content
            var scriptContent = script.ScriptContent;
            if (script.Variables != null)
            {
                foreach (var kvp in script.Variables)
                {
                    scriptContent = scriptContent.Replace($"{{{{{kvp.Key}}}}}", kvp.Value);
                }
            }

            // Write script to temp file
            var scriptPath = Path.Combine(workDir, "script.ps1");
            await File.WriteAllTextAsync(scriptPath, scriptContent);

            // Execute PowerShell
            var startedAt = DateTime.UtcNow;
            _logger.LogInformation("Executing PowerShell script for execution {ExecutionId} (timeout: {Timeout}s)",
                executionId, timeoutSeconds);
            var result = await RunPowerShellAsync(scriptPath, timeoutSeconds, workDir);
            var completedAt = DateTime.UtcNow;

            var status = result.TimedOut ? "timeout" :
                         result.ExitCode == 0 ? "success" : "failed";

            _logger.LogInformation(
                "Script execution {ExecutionId} finished: status={Status}, exitCode={ExitCode}, timedOut={TimedOut}",
                executionId, status, result.ExitCode, result.TimedOut);

            // Report result
            await ReportResultAsync(executionId, new ScriptResult
            {
                Status = status,
                ExitCode = result.ExitCode,
                Stdout = Truncate(result.Stdout, 256 * 1024),
                Stderr = Truncate(result.Stderr, 256 * 1024),
                StartedAt = startedAt,
                CompletedAt = completedAt
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Script execution {ExecutionId} failed with error", executionId);

            await ReportResultAsync(executionId, new ScriptResult
            {
                Status = "error",
                ExitCode = -1,
                Stdout = "",
                // L11: Sanitize before sending — strip filesystem paths, truncate to 512 chars
                Stderr = $"Agent execution error: {SanitizeErrorMessage(ex.Message)}",
                StartedAt = DateTime.UtcNow,
                CompletedAt = DateTime.UtcNow
            });
        }
        finally
        {
            // Clean up working directory
            try
            {
                if (Directory.Exists(workDir))
                    Directory.Delete(workDir, true);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to clean up work directory {WorkDir}", workDir);
            }
        }
    }

    private async Task<PowerShellResult> RunPowerShellAsync(string scriptPath, int timeoutSeconds, string workingDirectory)
    {
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var timedOut = false;

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\"",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = new Process { StartInfo = psi };

        process.OutputDataReceived += (s, e) =>
        {
            if (e.Data != null && stdout.Length < 256 * 1024)
                stdout.AppendLine(e.Data);
        };

        process.ErrorDataReceived += (s, e) =>
        {
            if (e.Data != null && stderr.Length < 256 * 1024)
                stderr.AppendLine(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var completed = await Task.Run(() => process.WaitForExit(timeoutSeconds * 1000));

        if (!completed)
        {
            timedOut = true;
            _logger.LogWarning("Script timed out after {Timeout}s, killing process tree", timeoutSeconds);
            try
            {
                process.Kill(entireProcessTree: true);
                // L10: Wait up to 5 seconds for the process tree to fully exit after Kill().
                // Without this, the process may still hold file handles when we delete workDir.
                await process.WaitForExitAsync(CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch { }
        }

        return new PowerShellResult
        {
            ExitCode = timedOut ? -1 : process.ExitCode,
            Stdout = stdout.ToString(),
            Stderr = stderr.ToString(),
            TimedOut = timedOut
        };
    }

    /// <summary>
    /// Allowed domains for helper file downloads. Files from external domains are rejected.
    /// </summary>
    private static readonly HashSet<string> AllowedDownloadDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "axis.gocbit.com"
    };

    /// <summary>
    /// Validates a server-supplied helper file filename to prevent path traversal.
    /// Returns the sanitized filename if valid, or null (and logs a security warning) if rejected.
    /// Rules: must equal Path.GetFileName() of itself (no directory components),
    /// must not be empty, start with '.', or contain dangerous characters.
    /// </summary>
    private string? ValidateHelperFilename(string rawFilename, string executionId)
    {
        if (string.IsNullOrEmpty(rawFilename))
        {
            _logger.LogError(
                "Script execution {ExecutionId}: helper file has empty filename — rejecting",
                executionId);
            return null;
        }

        // Path.GetFileName strips directory prefixes. If the result differs from the
        // original, the name contained path separators (\, /) — clear path traversal attempt.
        var leaf = Path.GetFileName(rawFilename);
        if (string.IsNullOrEmpty(leaf) || leaf != rawFilename)
        {
            _logger.LogError(
                "Script execution {ExecutionId}: helper file filename contains path components — rejecting: {Raw}",
                executionId, rawFilename);
            return null;
        }

        // Reject dot-files, double-dot traversal, and Windows-dangerous characters
        if (leaf.StartsWith('.') ||
            leaf.Contains("..") ||
            leaf.IndexOfAny(new[] { ':', '*', '?', '"', '<', '>', '|' }) >= 0)
        {
            _logger.LogError(
                "Script execution {ExecutionId}: helper file filename contains dangerous characters — rejecting: {Filename}",
                executionId, leaf);
            return null;
        }

        return leaf;
    }

    /// <summary>
    /// Downloads a helper file from a trusted domain, verifies its SHA-256 content hash
    /// against the value committed to in the signed payload, then writes it to disk.
    /// The file is never written to disk if the hash does not match.
    /// </summary>
    private async Task DownloadAndVerifyFileAsync(string url, string destinationPath,
        string expectedHashHex, string filename)
    {
        // Validate URL domain — only allow downloads from trusted domains
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new InvalidOperationException("Helper file URL is not a valid absolute URL");

        if (!AllowedDownloadDomains.Contains(uri.Host))
        {
            _logger.LogError("Helper file download rejected: domain {Domain} is not in the allowlist", uri.Host);
            throw new InvalidOperationException($"Helper file download rejected: domain '{uri.Host}' is not allowed");
        }

        // No Authorization header — helper file downloads are unauthenticated
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await SharedHttpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead);
        response.EnsureSuccessStatusCode();

        // Download to memory so we can hash before touching disk
        var fileBytes = await response.Content.ReadAsByteArrayAsync();

        // H2: Verify SHA-256 content hash against the value in the signed payload.
        // Convert.ToHexString returns uppercase; compare case-insensitively.
        var actualHashHex = Convert.ToHexString(SHA256.HashData(fileBytes));
        if (!string.Equals(actualHashHex, expectedHashHex, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogError(
                "Helper file integrity check failed for '{Filename}': expected {Expected}, got {Actual} — rejecting",
                filename, expectedHashHex.ToLowerInvariant(), actualHashHex.ToLowerInvariant());
            throw new InvalidOperationException(
                $"Helper file integrity check failed for {filename} — rejecting");
        }

        // Hash verified — safe to write to disk
        await File.WriteAllBytesAsync(destinationPath, fileBytes);
    }

    private async Task ReportResultAsync(string executionId, ScriptResult result)
    {
        try
        {
            await _apiClient.ReportScriptResultAsync(executionId, result);
            _logger.LogInformation("Reported script result for execution {ExecutionId}: {Status}",
                executionId, result.Status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to report script result for execution {ExecutionId}", executionId);
        }
    }

    /// <summary>
    /// L11: Sanitize exception messages before transmitting them to the server.
    /// Replaces Windows filesystem paths with [path] and truncates to 512 characters
    /// to avoid leaking directory structure or sensitive path components.
    /// </summary>
    private static string SanitizeErrorMessage(string? message)
    {
        if (string.IsNullOrEmpty(message)) return string.Empty;

        // Replace absolute Windows paths (e.g. C:\Users\admin\AppData\...)
        var sanitized = System.Text.RegularExpressions.Regex.Replace(
            message,
            @"[A-Za-z]:\\(?:[^\\/:\*\?""<>\|\r\n]+\\)*[^\\/:\*\?""<>\|\r\n]*",
            "[path]");

        // Replace UNC paths (\\server\share\...)
        sanitized = System.Text.RegularExpressions.Regex.Replace(
            sanitized,
            @"\\\\[^\\/\r\n]+(?:\\[^\\/\r\n]+)*",
            "[path]");

        // Truncate to 512 characters
        if (sanitized.Length > 512)
            sanitized = sanitized[..512];

        return sanitized;
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            return value;
        return value[..maxLength] + "\n\n--- OUTPUT TRUNCATED AT 256KB ---";
    }

    /// <summary>
    /// Verifies RSA-PSS SHA-256 signature over the full script payload:
    /// script_content + sorted variables + sorted helper file metadata.
    /// Server signs the same canonical payload with the customer's RSA private key.
    /// Canonical format: script_content + \n + sorted variables JSON + \n + sorted files JSON
    /// Variables: [["key","value"],...] sorted by key
    /// Files: [["filename","download_url"],...] sorted by filename
    /// Signature field is base64-encoded.
    /// </summary>
    private bool VerifyScriptSignature(PendingScript script, string? publicKeyPem)
    {
        if (string.IsNullOrEmpty(publicKeyPem))
        {
            _logger.LogError("Script rejected: no signing public key configured — reinstall agent");
            return false;
        }

        if (string.IsNullOrEmpty(script.ScriptSignature))
        {
            _logger.LogError("Script rejected: no signature provided in payload");
            return false;
        }

        // Build canonical payload: script_content + variables + files
        var payload = new StringBuilder();
        payload.Append(script.ScriptContent);

        // Append sorted variables as JSON array of [key, value] pairs
        payload.Append('\n');
        if (script.Variables != null && script.Variables.Count > 0)
        {
            var sorted = script.Variables.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new[] { kv.Key, kv.Value });
            payload.Append(JsonSerializer.Serialize(sorted));
        }
        else
        {
            payload.Append("[]");
        }

        // Append sorted files metadata as JSON array of [filename, download_url] pairs
        payload.Append('\n');
        if (script.Files != null && script.Files.Count > 0)
        {
            var sorted = script.Files.OrderBy(f => f.Filename, StringComparer.Ordinal)
                .Select(f => new[] { f.Filename, f.DownloadUrl });
            payload.Append(JsonSerializer.Serialize(sorted));
        }
        else
        {
            payload.Append("[]");
        }

        try
        {
            // PEM stored in config.json has literal \n — convert to real newlines
            var pem = publicKeyPem.Replace("\\n", "\n");

            using var rsa = RSA.Create();
            rsa.ImportFromPem(pem);

            var payloadBytes = Encoding.UTF8.GetBytes(payload.ToString());
            var signatureBytes = Convert.FromBase64String(script.ScriptSignature);

            // L7: Pss uses salt length = hash length (32 bytes for SHA-256) by .NET convention.
            // This is the PKCS#1 v2.2 recommended salt length. Explicit comment prevents
            // future ambiguity about which salt length this deployment expects.
            var valid = rsa.VerifyData(
                payloadBytes,
                signatureBytes,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pss); // salt = 32 bytes (SHA-256 hash length)

            if (!valid)
            {
                _logger.LogError("Script rejected: RSA-PSS signature verification failed — possible tampering");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Script rejected: signature verification error");
            return false;
        }
    }
}
