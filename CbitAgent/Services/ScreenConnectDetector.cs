using System.ServiceProcess;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace CbitAgent.Services;

public class ScreenConnectDetector
{
    private readonly ILogger<ScreenConnectDetector> _logger;

    public ScreenConnectDetector(ILogger<ScreenConnectDetector> logger)
    {
        _logger = logger;
    }

    public string? DetectGuid()
    {
        try
        {
            var services = ServiceController.GetServices();
            foreach (var svc in services)
            {
                try
                {
                    if (svc.ServiceName.StartsWith("ScreenConnect Client", StringComparison.OrdinalIgnoreCase))
                    {
                        // Service name format: "ScreenConnect Client ({guid})"
                        var match = Regex.Match(svc.ServiceName, @"\(([a-fA-F0-9\-]+)\)");
                        if (match.Success)
                        {
                            var guid = match.Groups[1].Value;
                            _logger.LogDebug("ScreenConnect detected, GUID: {Guid}", guid);
                            return guid;
                        }

                        // Try DisplayName as well
                        match = Regex.Match(svc.DisplayName, @"\(([a-fA-F0-9\-]+)\)");
                        if (match.Success)
                        {
                            var guid = match.Groups[1].Value;
                            _logger.LogDebug("ScreenConnect detected from display name, GUID: {Guid}", guid);
                            return guid;
                        }
                    }
                }
                finally
                {
                    svc.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to detect ScreenConnect");
        }

        _logger.LogDebug("ScreenConnect not detected");
        return null;
    }
}
