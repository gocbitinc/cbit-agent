using CbitAgent.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace CbitAgent.Services;

public class InstalledAppsCollector
{
    private readonly ILogger<InstalledAppsCollector> _logger;

    private static readonly string[] RegistryPaths = new[]
    {
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
    };

    public InstalledAppsCollector(ILogger<InstalledAppsCollector> logger)
    {
        _logger = logger;
    }

    public List<InstalledApp> Collect()
    {
        var apps = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);

        // HKLM paths
        foreach (var path in RegistryPaths)
        {
            CollectFromKey(Registry.LocalMachine, path, apps);
        }

        // HKCU path
        CollectFromKey(Registry.CurrentUser, RegistryPaths[0], apps);

        var result = apps.Values
            .OrderBy(a => a.Name)
            .ToList();

        _logger.LogInformation("Collected {Count} installed applications", result.Count);
        return result;
    }

    private void CollectFromKey(RegistryKey root, string path, Dictionary<string, InstalledApp> apps)
    {
        try
        {
            using var key = root.OpenSubKey(path);
            if (key == null) return;

            foreach (var subKeyName in key.GetSubKeyNames())
            {
                try
                {
                    using var subKey = key.OpenSubKey(subKeyName);
                    if (subKey == null) continue;

                    var displayName = subKey.GetValue("DisplayName")?.ToString();
                    if (string.IsNullOrWhiteSpace(displayName)) continue;

                    // Skip system components
                    var systemComponent = subKey.GetValue("SystemComponent");
                    if (systemComponent != null && Convert.ToInt32(systemComponent) == 1)
                        continue;

                    // Skip sub-components
                    var parentKeyName = subKey.GetValue("ParentKeyName")?.ToString();
                    if (!string.IsNullOrEmpty(parentKeyName)) continue;

                    var version = subKey.GetValue("DisplayVersion")?.ToString();
                    var publisher = subKey.GetValue("Publisher")?.ToString();
                    var installDateStr = subKey.GetValue("InstallDate")?.ToString();

                    string? installDate = null;
                    if (!string.IsNullOrEmpty(installDateStr) && installDateStr.Length == 8)
                    {
                        // Parse YYYYMMDD to YYYY-MM-DD
                        installDate = $"{installDateStr[..4]}-{installDateStr[4..6]}-{installDateStr[6..8]}";
                    }

                    // Deduplicate: keep the entry with more info
                    if (apps.TryGetValue(displayName, out var existing))
                    {
                        var existingScore = Score(existing);
                        var newScore = ScoreValues(version, publisher, installDate);
                        if (newScore <= existingScore) continue;
                    }

                    apps[displayName] = new InstalledApp
                    {
                        Name = displayName,
                        Version = version,
                        Publisher = publisher,
                        InstallDate = installDate
                    };
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to read registry subkey {Key}", subKeyName);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to enumerate registry path {Path}", path);
        }
    }

    private static int Score(InstalledApp app)
    {
        return ScoreValues(app.Version, app.Publisher, app.InstallDate);
    }

    private static int ScoreValues(string? version, string? publisher, string? installDate)
    {
        int score = 0;
        if (!string.IsNullOrEmpty(version)) score++;
        if (!string.IsNullOrEmpty(publisher)) score++;
        if (!string.IsNullOrEmpty(installDate)) score++;
        return score;
    }
}
