using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
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

        // Verify HMAC-SHA256 signature before any execution
        var signingSecret = _configManager.Config.ScriptSigningSecret;
        if (!VerifyScriptSignature(script.ScriptContent, script.ScriptSignature, signingSecret))
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
                    var filePath = Path.Combine(workDir, file.Filename);
                    _logger.LogInformation("Downloading file {Filename} for execution {ExecutionId}",
                        file.Filename, executionId);
                    await DownloadFileAsync(file.DownloadUrl, filePath);
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
                Stderr = $"Agent execution error: {ex.Message}",
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
            try { process.Kill(entireProcessTree: true); } catch { }
        }

        return new PowerShellResult
        {
            ExitCode = timedOut ? -1 : process.ExitCode,
            Stdout = stdout.ToString(),
            Stderr = stderr.ToString(),
            TimedOut = timedOut
        };
    }

    private async Task DownloadFileAsync(string url, string destinationPath)
    {
        var config = _configManager.Config;
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.AgentToken);

        using var response = await SharedHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        await using var fileStream = File.Create(destinationPath);
        await response.Content.CopyToAsync(fileStream);
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

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            return value;
        return value[..maxLength] + "\n\n--- OUTPUT TRUNCATED AT 256KB ---";
    }

    private bool VerifyScriptSignature(string scriptContent, string? signature, string? secret)
    {
        if (string.IsNullOrEmpty(secret))
        {
            _logger.LogError("Script rejected: no signing secret configured — re-register agent");
            return false;
        }

        if (string.IsNullOrEmpty(signature))
        {
            _logger.LogError("Script rejected: no signature provided in payload");
            return false;
        }

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(scriptContent));
        var computed = Convert.ToHexString(hash).ToLowerInvariant();

        if (!CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(computed),
            Encoding.UTF8.GetBytes(signature)))
        {
            _logger.LogError("Script rejected: HMAC signature verification failed — possible tampering");
            return false;
        }

        return true;
    }
}
