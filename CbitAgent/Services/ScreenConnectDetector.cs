using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace CbitAgent.Services;

public class ScreenConnectDetector
{
    private const string DefaultInstanceId = "8646a2c674847db0";
    private readonly ILogger<ScreenConnectDetector> _logger;

    public ScreenConnectDetector(ILogger<ScreenConnectDetector> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Reads the ScreenConnect session GUID from the service's ImagePath registry value.
    /// The ImagePath contains command-line arguments including s=GUID.
    /// </summary>
    public string? DetectGuid(string? configuredInstanceId = null)
    {
        var instanceId = string.IsNullOrEmpty(configuredInstanceId) ? DefaultInstanceId : configuredInstanceId;

        try
        {
            var serviceName = $"ScreenConnect Client ({instanceId})";
            var registryPath = $@"SYSTEM\CurrentControlSet\Services\{serviceName}";

            using var key = Registry.LocalMachine.OpenSubKey(registryPath);
            if (key == null)
            {
                _logger.LogDebug("ScreenConnect service registry key not found: {Path}", registryPath);
                return null;
            }

            var imagePath = key.GetValue("ImagePath") as string;
            if (string.IsNullOrEmpty(imagePath))
            {
                _logger.LogDebug("ScreenConnect ImagePath is empty for {Service}", serviceName);
                return null;
            }

            // Extract the s= parameter value (session GUID) from the ImagePath
            // ImagePath format: "...ScreenConnect.ClientService.exe" e=Access&y=Guest&h=host&p=443&s=GUID-HERE&...
            var match = Regex.Match(imagePath, @"[&\s]s=([0-9a-fA-F\-]{36})");
            if (match.Success)
            {
                var sessionGuid = match.Groups[1].Value;
                _logger.LogDebug("ScreenConnect session GUID detected: {Guid}", sessionGuid);
                return sessionGuid;
            }

            _logger.LogDebug("Could not extract s= parameter from ScreenConnect ImagePath");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to detect ScreenConnect session GUID");
            return null;
        }
    }
}
