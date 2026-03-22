using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.ServiceProcess;
using CbitAgent.Models;
using Microsoft.Extensions.Logging;

namespace CbitAgent.Services;

public class ServiceMonitor
{
    private readonly ILogger<ServiceMonitor> _logger;

    // In-memory tracking of services currently in down state
    // Key: service name, Value: DateTime when it first went down
    private readonly Dictionary<string, DateTime> _downSince = new();

    // Track last check time per event log for dedup
    private readonly Dictionary<string, DateTime> _lastEventCheck = new();

    private const int RestartWaitSeconds = 10;
    private const int TicketDelaySeconds = 120;

    public ServiceMonitor(ILogger<ServiceMonitor> logger)
    {
        _logger = logger;
    }

    public List<ServiceAlertPayload> CheckServices(string installDir, string assetId)
    {
        var alerts = new List<ServiceAlertPayload>();

        var config = ServiceMonitorConfig.Load(installDir, _logger);
        if (config == null || config.Services.Count == 0)
            return alerts;

        foreach (var serviceName in config.Services)
        {
            try
            {
                CheckSingleService(serviceName, assetId, alerts);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to check service {ServiceName}", serviceName);
            }
        }

        return alerts;
    }

    private void CheckSingleService(string serviceName, string assetId, List<ServiceAlertPayload> alerts)
    {
        ServiceControllerStatus status;
        try
        {
            using var sc = new ServiceController(serviceName);
            status = sc.Status;
        }
        catch (InvalidOperationException)
        {
            _logger.LogWarning("Service {ServiceName} not found on this machine", serviceName);
            return;
        }

        if (status == ServiceControllerStatus.Running)
        {
            // Service is running — check if it was previously down (recovery)
            if (_downSince.Remove(serviceName))
            {
                _logger.LogInformation("Service {ServiceName} has recovered", serviceName);
                alerts.Add(new ServiceAlertPayload
                {
                    AssetId = assetId,
                    ServiceName = serviceName,
                    Status = "recovered",
                    Message = $"Service '{serviceName}' has recovered and is now running.",
                    Timestamp = DateTime.UtcNow
                });
            }
            return;
        }

        if (status != ServiceControllerStatus.Stopped)
            return;

        if (!_downSince.ContainsKey(serviceName))
        {
            // First time seeing it stopped — attempt restart via sc.exe
            // (ServiceController.Start() gets Access Denied even under LocalSystem;
            //  sc.exe runs with the full LocalSystem token and succeeds)
            _logger.LogWarning("Service {ServiceName} is stopped, attempting restart via sc.exe...", serviceName);
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "sc.exe",
                    Arguments = $"start \"{serviceName}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit(RestartWaitSeconds * 1000);

                // Verify service actually started
                using var sc = new ServiceController(serviceName);
                sc.Refresh();
                if (sc.Status == ServiceControllerStatus.Running)
                {
                    _logger.LogInformation("Service {ServiceName} restarted successfully via sc.exe", serviceName);
                    return; // Restart succeeded, no alert needed
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to restart service {ServiceName} via sc.exe", serviceName);
            }

            // Still stopped after restart attempt — start tracking
            _downSince[serviceName] = DateTime.UtcNow;
            _logger.LogInformation("Service {ServiceName} added to down tracking", serviceName);
        }
        else
        {
            // Already tracking — check if ticket delay has elapsed
            var downTime = DateTime.UtcNow - _downSince[serviceName];
            if (downTime >= TimeSpan.FromSeconds(TicketDelaySeconds))
            {
                _logger.LogWarning("Service {ServiceName} has been down for {Seconds}s, raising alert",
                    serviceName, (int)downTime.TotalSeconds);
                alerts.Add(new ServiceAlertPayload
                {
                    AssetId = assetId,
                    ServiceName = serviceName,
                    Status = "down",
                    Message = $"Service '{serviceName}' has been stopped for {(int)downTime.TotalSeconds} seconds and could not be restarted.",
                    Timestamp = DateTime.UtcNow
                });

                // Remove from tracking to avoid repeat alerts
                _downSince.Remove(serviceName);
            }
        }
    }

    public List<EventAlertPayload> CheckEvents(string installDir, string assetId)
    {
        var alerts = new List<EventAlertPayload>();

        var config = ServiceMonitorConfig.Load(installDir, _logger);
        if (config == null || config.Events.Count == 0)
            return alerts;

        foreach (var entry in config.Events)
        {
            try
            {
                CheckSingleEventLog(entry, assetId, alerts);
            }
            catch (UnauthorizedAccessException)
            {
                _logger.LogWarning(
                    "Access denied reading {LogName} event log for EventID {EventId}. " +
                    "Security event log requires SeSecurityPrivilege. " +
                    "Grant this privilege to the CbitRmmAgent service or run as Administrator.",
                    entry.LogName, entry.EventId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to check event log {LogName} for EventID {EventId}",
                    entry.LogName, entry.EventId);
            }
        }

        return alerts;
    }

    private void CheckSingleEventLog(EventWatchEntry entry, string assetId, List<EventAlertPayload> alerts)
    {
        var key = $"{entry.LogName}:{entry.EventId}";

        if (!_lastEventCheck.TryGetValue(key, out var lastCheck))
            lastCheck = DateTime.UtcNow.AddMinutes(-5); // Default: 5 minutes ago on first run

        var queryString = $"*[System[EventID={entry.EventId} and TimeCreated[@SystemTime>='{lastCheck:o}']]]";
        var query = new EventLogQuery(entry.LogName, PathType.LogName, queryString)
        {
            // Use explicit EventLogSession to access privileged logs (e.g. Security)
            // under the full LocalSystem token rather than the default reader context
            Session = new EventLogSession(
                ".",
                null,
                null,
                null,
                SessionAuthentication.Default)
        };

        using var reader = new EventLogReader(query);
        EventRecord? record;
        while ((record = reader.ReadEvent()) != null)
        {
            using (record)
            {
                var message = string.Empty;
                try { message = record.FormatDescription() ?? string.Empty; }
                catch { /* Some events don't have format strings */ }

                alerts.Add(new EventAlertPayload
                {
                    AssetId = assetId,
                    EventId = entry.EventId,
                    EventLog = entry.LogName,
                    Message = message.Length > 1024 ? message[..1024] : message,
                    Timestamp = record.TimeCreated?.ToUniversalTime() ?? DateTime.UtcNow
                });
            }
        }

        _lastEventCheck[key] = DateTime.UtcNow;
    }
}
