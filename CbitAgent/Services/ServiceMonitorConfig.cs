using Microsoft.Extensions.Logging;

namespace CbitAgent.Services;

public class ServiceMonitorConfig
{
    public List<string> Services { get; set; } = new();
    public List<EventWatchEntry> Events { get; set; } = new();

    public static ServiceMonitorConfig? Load(string installDir, ILogger logger)
    {
        var path = Path.Combine(installDir, "service-monitor.ini");
        if (!File.Exists(path))
        {
            logger.LogDebug("service-monitor.ini not found at {Path}", path);
            return null;
        }

        var config = new ServiceMonitorConfig();
        string? currentSection = null;

        try
        {
            foreach (var rawLine in File.ReadAllLines(path))
            {
                var line = rawLine.Trim();

                // Skip empty lines and comments
                if (string.IsNullOrEmpty(line) || line.StartsWith(';'))
                    continue;

                // Section headers
                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    currentSection = line[1..^1].ToLowerInvariant();
                    continue;
                }

                if (currentSection == "services")
                {
                    config.Services.Add(line);
                }
                else if (currentSection == "events")
                {
                    // Format: EventID=4625, log=Security
                    var entry = ParseEventLine(line);
                    if (entry != null)
                        config.Events.Add(entry);
                    else
                        logger.LogWarning("Invalid event line in service-monitor.ini: {Line}", line);
                }
            }

            logger.LogInformation("Loaded service-monitor.ini: {ServiceCount} services, {EventCount} events",
                config.Services.Count, config.Events.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to parse service-monitor.ini at {Path}", path);
            return null;
        }

        return config;
    }

    private static EventWatchEntry? ParseEventLine(string line)
    {
        // Expected format: EventID=4625, log=Security
        string? eventId = null;
        string? logName = null;

        var parts = line.Split(',', StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            var kvp = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (kvp.Length != 2) continue;

            if (kvp[0].Equals("EventID", StringComparison.OrdinalIgnoreCase))
                eventId = kvp[1];
            else if (kvp[0].Equals("log", StringComparison.OrdinalIgnoreCase))
                logName = kvp[1];
        }

        if (eventId != null && logName != null)
            return new EventWatchEntry { EventId = eventId, LogName = logName };

        return null;
    }
}

public class EventWatchEntry
{
    public string EventId { get; set; } = string.Empty;
    public string LogName { get; set; } = string.Empty;
}
