using System.Diagnostics;
using System.Security.Cryptography;
using CbitAgent.Configuration;
using CbitAgent.Models;
using Microsoft.Extensions.Logging;

namespace CbitAgent.Services;

public class AgentUpdater
{
    private readonly ApiClient _apiClient;
    private readonly ConfigManager _configManager;
    private readonly ILogger<AgentUpdater> _logger;

    public AgentUpdater(ApiClient apiClient, ConfigManager configManager, ILogger<AgentUpdater> logger)
    {
        _apiClient = apiClient;
        _configManager = configManager;
        _logger = logger;
    }

    public async Task<bool> ProcessUpdateCommandAsync(AgentCommand command, CancellationToken ct)
    {
        if (command.Type != "update_agent" || string.IsNullOrEmpty(command.DownloadUrl))
        {
            _logger.LogWarning("Invalid update command");
            return false;
        }

        var targetVersion = command.Version ?? "unknown";
        var currentVersion = GetCurrentVersion();
        var installDir = AppContext.BaseDirectory;
        var stagingDir = Path.Combine(installDir, "staging");
        var backupDir = Path.Combine(installDir, "backup");

        _logger.LogInformation("Agent update requested: {Current} -> {Target}", currentVersion, targetVersion);

        try
        {
            // Prepare directories
            Directory.CreateDirectory(stagingDir);
            Directory.CreateDirectory(backupDir);

            // Step 1: Download the new binary
            var downloadPath = Path.Combine(stagingDir, "CbitAgent.exe");
            var downloaded = await _apiClient.DownloadFileAsync(command.DownloadUrl, downloadPath, ct);
            if (!downloaded)
            {
                _logger.LogError("Failed to download update file");
                await ReportUpdateResult(currentVersion, targetVersion, "failed", "Download failed");
                return false;
            }

            // Step 2: Verify SHA256 hash
            if (!string.IsNullOrEmpty(command.FileHash))
            {
                var expectedHash = command.FileHash;
                if (expectedHash.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    expectedHash = expectedHash["sha256:".Length..];

                var actualHash = await ComputeFileHashAsync(downloadPath);
                if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogError("Hash mismatch. Expected: {Expected}, Got: {Actual}", expectedHash, actualHash);
                    await ReportUpdateResult(currentVersion, targetVersion, "failed",
                        $"Hash mismatch: expected {expectedHash}, got {actualHash}");
                    CleanupStaging(stagingDir);
                    return false;
                }

                _logger.LogInformation("File hash verified successfully");
            }

            // Step 3: Backup current binary
            var currentExePath = Process.GetCurrentProcess().MainModule?.FileName
                                 ?? Path.Combine(installDir, "CbitAgent.exe");
            var backupPath = Path.Combine(backupDir, "CbitAgent.exe");

            try
            {
                File.Copy(currentExePath, backupPath, overwrite: true);
                _logger.LogInformation("Current binary backed up to {Path}", backupPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to backup current binary, continuing anyway");
            }

            // Step 4: Write the update batch script
            var updateScriptPath = Path.Combine(installDir, "update.cmd");
            var serviceName = "CBIT RMM Agent";

            var script = $"""
                @echo off
                echo Updating CBIT Agent to version {targetVersion}...
                timeout /t 3 /nobreak >nul

                REM Try to copy new binary
                copy /Y "{downloadPath}" "{currentExePath}"
                if errorlevel 1 (
                    echo Failed to copy new binary, attempting rollback...
                    copy /Y "{backupPath}" "{currentExePath}"
                    echo Starting service with original binary...
                    net start "{serviceName}"
                    exit /b 1
                )

                echo Starting service with new binary...
                net start "{serviceName}"

                REM Cleanup staging
                if exist "{stagingDir}" rmdir /s /q "{stagingDir}"

                echo Update complete.
                """;

            File.WriteAllText(updateScriptPath, script);
            _logger.LogInformation("Update script written to {Path}", updateScriptPath);

            // Step 5: Save pending update info for post-restart reporting
            var updateInfoPath = Path.Combine(installDir, "pending_update.json");
            var updateInfo = System.Text.Json.JsonSerializer.Serialize(new
            {
                from_version = currentVersion,
                to_version = targetVersion,
                timestamp = DateTime.UtcNow
            });
            File.WriteAllText(updateInfoPath, updateInfo);

            // Step 6: Launch the update script and stop the service
            _logger.LogInformation("Launching update script and stopping service...");

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{updateScriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = installDir
            };

            Process.Start(psi);

            // The service will be stopped by the Worker's cancellation.
            // The batch script waits, replaces the binary, then restarts.
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Agent update failed");
            await ReportUpdateResult(currentVersion, targetVersion, "failed", ex.Message);
            CleanupStaging(stagingDir);
            return false;
        }
    }

    /// <summary>
    /// Called on startup to check if we just completed an update and need to report the result.
    /// </summary>
    public async Task CheckPendingUpdateResultAsync(CancellationToken ct)
    {
        var updateInfoPath = Path.Combine(AppContext.BaseDirectory, "pending_update.json");
        if (!File.Exists(updateInfoPath)) return;

        try
        {
            var json = await File.ReadAllTextAsync(updateInfoPath, ct);
            var updateInfo = System.Text.Json.JsonSerializer.Deserialize<PendingUpdateInfo>(json);

            if (updateInfo == null) return;

            var currentVersion = GetCurrentVersion();
            var success = currentVersion == updateInfo.ToVersion;

            _logger.LogInformation("Post-update check: expected {Expected}, running {Actual}, success={Success}",
                updateInfo.ToVersion, currentVersion, success);

            await ReportUpdateResult(
                updateInfo.FromVersion ?? "unknown",
                updateInfo.ToVersion ?? "unknown",
                success ? "success" : "failed",
                success ? null : $"Running version {currentVersion} instead of expected {updateInfo.ToVersion}");

            // Cleanup
            File.Delete(updateInfoPath);

            // If update failed and backup exists, the watchdog/batch script should have restored it
            if (!success)
            {
                _logger.LogWarning("Update may have failed. Running version {Current} instead of {Expected}",
                    currentVersion, updateInfo.ToVersion);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process pending update result");
            try { File.Delete(updateInfoPath); } catch { }
        }
    }

    private async Task ReportUpdateResult(string from, string to, string status, string? error)
    {
        try
        {
            var agentId = _configManager.Config.AgentId;
            if (!string.IsNullOrEmpty(agentId))
            {
                await _apiClient.ReportUpdateResultAsync(agentId, from, to, status, error);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to report update result to server");
        }
    }

    private static async Task<string> ComputeFileHashAsync(string filePath)
    {
        using var sha256 = SHA256.Create();
        await using var stream = File.OpenRead(filePath);
        var hash = await sha256.ComputeHashAsync(stream);
        return BitConverter.ToString(hash).Replace("-", "").ToLower();
    }

    private static string GetCurrentVersion()
    {
        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        var version = assembly.GetName().Version;
        return version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "1.0.0";
    }

    private void CleanupStaging(string stagingDir)
    {
        try
        {
            if (Directory.Exists(stagingDir))
                Directory.Delete(stagingDir, recursive: true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to clean up staging directory");
        }
    }

    private class PendingUpdateInfo
    {
        public string? FromVersion { get; set; }
        public string? ToVersion { get; set; }
        public DateTime? Timestamp { get; set; }

        // Support JSON property names from serialization
        [System.Text.Json.Serialization.JsonPropertyName("from_version")]
        public string? FromVersionJson { set => FromVersion = value; }

        [System.Text.Json.Serialization.JsonPropertyName("to_version")]
        public string? ToVersionJson { set => ToVersion = value; }

        [System.Text.Json.Serialization.JsonPropertyName("timestamp")]
        public DateTime? TimestampJson { set => Timestamp = value; }
    }
}
