using CbitAgent.Configuration;
using CbitAgent.Models;
using CbitAgent.Services;

namespace CbitAgent;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly ConfigManager _configManager;
    private readonly ApiClient _apiClient;
    private readonly SystemInfoCollector _systemInfoCollector;
    private readonly NetworkInfoCollector _networkInfoCollector;
    private readonly DiskInfoCollector _diskInfoCollector;
    private readonly InstalledAppsCollector _installedAppsCollector;
    private readonly PatchInfoCollector _patchInfoCollector;
    private readonly ScreenConnectDetector _screenConnectDetector;
    private readonly AgentUpdater _agentUpdater;
    private readonly WebSocketTerminalClient _wsTerminalClient;
    private readonly WindowsUpdateExecutor _windowsUpdateExecutor;
    private readonly ScriptExecutor _scriptExecutor;

    private int _checkInCount;
    private bool _scriptInProgress;
    private const int AppsReportInterval = 12;   // Every 12th check-in (~1 hour at 5 min intervals)
    private const int PatchReportInterval = 12;   // Every 12th check-in

    public Worker(
        ILogger<Worker> logger,
        ConfigManager configManager,
        ApiClient apiClient,
        SystemInfoCollector systemInfoCollector,
        NetworkInfoCollector networkInfoCollector,
        DiskInfoCollector diskInfoCollector,
        InstalledAppsCollector installedAppsCollector,
        PatchInfoCollector patchInfoCollector,
        ScreenConnectDetector screenConnectDetector,
        AgentUpdater agentUpdater,
        WebSocketTerminalClient wsTerminalClient,
        WindowsUpdateExecutor windowsUpdateExecutor,
        ScriptExecutor scriptExecutor)
    {
        _logger = logger;
        _configManager = configManager;
        _apiClient = apiClient;
        _systemInfoCollector = systemInfoCollector;
        _networkInfoCollector = networkInfoCollector;
        _diskInfoCollector = diskInfoCollector;
        _installedAppsCollector = installedAppsCollector;
        _patchInfoCollector = patchInfoCollector;
        _screenConnectDetector = screenConnectDetector;
        _agentUpdater = agentUpdater;
        _wsTerminalClient = wsTerminalClient;
        _windowsUpdateExecutor = windowsUpdateExecutor;
        _scriptExecutor = scriptExecutor;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("CBIT RMM Agent starting...");

        // Load configuration
        _configManager.Load();
        var config = _configManager.Config;

        if (string.IsNullOrEmpty(config.CustomerKey) && !config.IsRegistered)
        {
            _logger.LogError("No customer_key configured and agent is not registered. " +
                             "Set customer_key in config.json to register with the server.");
            return;
        }

        // Register if needed
        if (!config.IsRegistered)
        {
            var registered = await RegisterAsync(stoppingToken);
            if (!registered)
            {
                _logger.LogError("Failed to register with server. Agent will retry on next start.");
                return;
            }
        }

        // Check for pending update result from a previous update
        await _agentUpdater.CheckPendingUpdateResultAsync(stoppingToken);

        // Report apps and patches on first check-in
        _checkInCount = 0;

        _logger.LogInformation("Agent registered as {AgentId}, starting check-in loop (interval: {Interval} min)",
            config.AgentId, config.CheckInIntervalMinutes);

        // Start WebSocket terminal client alongside the check-in loop
        var wsTask = _wsTerminalClient.RunAsync(stoppingToken);

        // Main check-in loop
        var checkInTask = RunCheckInLoopAsync(stoppingToken);

        // Wait for either task to complete (both run until cancellation)
        await Task.WhenAll(checkInTask, wsTask);

        _logger.LogInformation("CBIT RMM Agent stopping.");
    }

    private async Task RunCheckInLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PerformCheckInAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Check-in failed, will retry next interval");
            }

            var interval = TimeSpan.FromMinutes(_configManager.Config.CheckInIntervalMinutes);
            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task<bool> RegisterAsync(CancellationToken ct)
    {
        var config = _configManager.Config;
        _logger.LogInformation("Registering agent with server at {Url}...", config.ServerUrl);

        // Collect basic system info for registration
        var sysInfo = _systemInfoCollector.Collect();

        var response = await _apiClient.RegisterAsync(
            config.CustomerKey,
            sysInfo.Hostname,
            sysInfo.OsType,
            sysInfo.OsVersion,
            GetCurrentVersion(),
            ct);

        if (response == null || string.IsNullOrEmpty(response.AgentId))
        {
            _logger.LogError("Registration failed: no response or empty agent_id");
            return false;
        }

        _configManager.UpdateRegistration(response.AgentId, response.AgentToken, response.CheckInIntervalMinutes);
        _logger.LogInformation("Registration successful. Agent ID: {AgentId}", response.AgentId);
        return true;
    }

    private async Task PerformCheckInAsync(CancellationToken ct)
    {
        _checkInCount++;
        var config = _configManager.Config;

        _logger.LogInformation("Performing check-in #{Count}...", _checkInCount);

        // Collect all data
        var systemInfo = _systemInfoCollector.Collect();
        var networkAdapters = _networkInfoCollector.CollectAdapters();
        var disks = _diskInfoCollector.CollectDisks();
        var smartData = _diskInfoCollector.CollectSmartData();
        var screenConnectGuid = _screenConnectDetector.DetectGuid();
        var wanIp = await _networkInfoCollector.GetWanIpAsync(ct);

        var payload = new CheckInPayload
        {
            AgentId = config.AgentId!,
            AgentVersion = GetCurrentVersion(),
            Timestamp = DateTime.UtcNow,
            SystemInfo = systemInfo,
            NetworkAdapters = networkAdapters,
            Disks = disks,
            SmartData = smartData,
            ScreenConnectGuid = screenConnectGuid,
            WanIp = wanIp
        };

        var response = await _apiClient.CheckInAsync(payload, ct);

        if (response == null)
        {
            _logger.LogWarning("Check-in received no response from server");
            return;
        }

        _logger.LogInformation("Check-in successful. Status: {Status}, Commands: {CommandCount}",
            response.Status, response.Commands.Count);

        // Update check-in interval if server says something different
        if (response.CheckInIntervalMinutes > 0 &&
            response.CheckInIntervalMinutes != config.CheckInIntervalMinutes)
        {
            _logger.LogInformation("Updating check-in interval to {Interval} minutes",
                response.CheckInIntervalMinutes);
            config.CheckInIntervalMinutes = response.CheckInIntervalMinutes;
            _configManager.Save();
        }

        // Process commands
        await ProcessCommandsAsync(response.Commands, ct);

        // Handle pending script execution
        if (response.PendingScript != null && !_scriptInProgress)
        {
            _logger.LogInformation("Received pending script execution {ExecutionId}",
                response.PendingScript.ExecutionId);
            _scriptInProgress = true;
            _ = Task.Run(async () =>
            {
                try
                {
                    await _scriptExecutor.ExecuteAsync(response.PendingScript);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Script execution {ExecutionId} threw unhandled exception",
                        response.PendingScript.ExecutionId);
                }
                finally
                {
                    _scriptInProgress = false;
                }
            });
        }
        else if (response.PendingScript != null && _scriptInProgress)
        {
            _logger.LogInformation("Script already in progress, skipping pending script {ExecutionId}",
                response.PendingScript.ExecutionId);
        }

        // Periodic reports
        if (_checkInCount % AppsReportInterval == 0 || _checkInCount == 1)
        {
            await ReportAppsAsync(ct);
        }

        if (_checkInCount % PatchReportInterval == 0 || _checkInCount == 1)
        {
            await ReportPatchesAsync(ct);
        }
    }

    private async Task ProcessCommandsAsync(List<AgentCommand> commands, CancellationToken ct)
    {
        foreach (var command in commands)
        {
            try
            {
                switch (command.Type)
                {
                    case "update_agent":
                        _logger.LogInformation("Received update_agent command for version {Version}",
                            command.Version);
                        var updateStarted = await _agentUpdater.ProcessUpdateCommandAsync(command, ct);
                        if (updateStarted)
                        {
                            _logger.LogInformation("Update process initiated, service will restart...");
                            return;
                        }
                        break;

                    case "install_kb":
                        _logger.LogInformation("Received install_kb command for {Kb}", command.KbNumber);
                        _ = HandleInstallKbAsync(command, ct);
                        break;

                    case "run_updates":
                        _logger.LogInformation("Received run_updates command with policy {PolicyId}",
                            command.PolicyId);
                        _ = HandleRunUpdatesAsync(command, ct);
                        break;

                    default:
                        _logger.LogWarning("Unknown command type: {Type}", command.Type);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing command: {Type}", command.Type);
            }
        }
    }

    private async Task ReportAppsAsync(CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("Collecting and reporting installed applications...");
            var apps = _installedAppsCollector.Collect();
            var success = await _apiClient.ReportAppsAsync(_configManager.Config.AgentId!, apps, ct);
            _logger.LogInformation("Apps report: {Result} ({Count} apps)",
                success ? "sent" : "failed", apps.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to report installed apps");
        }
    }

    private async Task ReportPatchesAsync(CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("Collecting and reporting patch status...");
            var installed = _patchInfoCollector.CollectInstalledPatches();
            var pending = _patchInfoCollector.CollectPendingPatches();
            var success = await _apiClient.ReportPatchesAsync(
                _configManager.Config.AgentId!, installed, pending, ct);
            _logger.LogInformation("Patch report: {Result} ({Installed} installed, {Pending} pending)",
                success ? "sent" : "failed", installed.Count, pending.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to report patch status");
        }
    }

    private async Task HandleInstallKbAsync(AgentCommand command, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(command.KbNumber)) return;

        var startedAt = DateTime.UtcNow;
        try
        {
            var result = await _windowsUpdateExecutor.InstallUpdatesAsync(
                new List<string> { command.KbNumber }, null, null, ct);

            await _apiClient.ReportUpdateJobResultAsync(
                _configManager.Config.AgentId!, "adhoc", null, command.JobId,
                startedAt, DateTime.UtcNow, result.Status,
                result.KbsInstalled, result.KbsFailed,
                result.ErrorMessage, result.RebootRequired, ct);

            // Refresh patch status after install
            await ReportPatchesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle install_kb for {Kb}", command.KbNumber);
            await _apiClient.ReportUpdateJobResultAsync(
                _configManager.Config.AgentId!, "adhoc", null, command.JobId,
                startedAt, DateTime.UtcNow, "failed",
                new List<string>(), new List<string> { command.KbNumber },
                ex.Message, false, ct);
        }
    }

    private async Task HandleRunUpdatesAsync(AgentCommand command, CancellationToken ct)
    {
        var startedAt = DateTime.UtcNow;
        try
        {
            // Convert Dictionary<string, object> policy to JsonElement
            System.Text.Json.JsonElement? policyElement = null;
            if (command.Policy != null)
            {
                var json = System.Text.Json.JsonSerializer.Serialize(command.Policy);
                policyElement = System.Text.Json.JsonDocument.Parse(json).RootElement.Clone();
            }

            var result = await _windowsUpdateExecutor.RunPolicyUpdatesAsync(
                policyElement, command.PolicyId, null, ct);

            await _apiClient.ReportUpdateJobResultAsync(
                _configManager.Config.AgentId!, "scheduled", command.PolicyId, null,
                startedAt, DateTime.UtcNow, result.Status,
                result.KbsInstalled, result.KbsFailed,
                result.ErrorMessage, result.RebootRequired, ct);

            // Refresh patch status after install
            await ReportPatchesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle run_updates for policy {PolicyId}", command.PolicyId);
            await _apiClient.ReportUpdateJobResultAsync(
                _configManager.Config.AgentId!, "scheduled", command.PolicyId, null,
                startedAt, DateTime.UtcNow, "failed",
                new List<string>(), new List<string>(),
                ex.Message, false, ct);
        }
    }

    private static string GetCurrentVersion()
    {
        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        var version = assembly.GetName().Version;
        return version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "1.0.0";
    }
}
