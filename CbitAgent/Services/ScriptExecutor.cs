using System.Diagnostics;
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

    public ScriptExecutor(ApiClient apiClient, ConfigManager configManager, ILogger<ScriptExecutor> logger)
    {
        _apiClient = apiClient;
        _configManager = configManager;
        _logger = logger;
    }

    public async Task ExecuteAsync(PendingScript script)
    {
        var executionId = script.ExecutionId;
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
                executionId, script.TimeoutSeconds);
            var result = await RunPowerShellAsync(scriptPath, script.TimeoutSeconds, workDir);
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
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.AgentToken);

        using var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        await using var fileStream = File.Create(destinationPath);
        await response.Content.CopyToAsync(fileStream);
    }

    private async Task ReportResultAsync(int executionId, ScriptResult result)
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
}
