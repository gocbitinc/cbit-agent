#pragma warning disable CS8602 // COM interop dynamic member access
using System.Management;
using System.Runtime.InteropServices;
using CbitAgent.Models;
using Microsoft.Extensions.Logging;

namespace CbitAgent.Services;

public class PatchInfoCollector
{
    private readonly ILogger<PatchInfoCollector> _logger;

    public PatchInfoCollector(ILogger<PatchInfoCollector> logger)
    {
        _logger = logger;
    }

    public List<InstalledPatch> CollectInstalledPatches()
    {
        var patches = new List<InstalledPatch>();

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT HotFixID, Description, InstalledOn FROM Win32_QuickFixEngineering");

            foreach (var obj in searcher.Get())
            {
                var kbNumber = obj["HotFixID"]?.ToString();
                if (string.IsNullOrEmpty(kbNumber)) continue;

                DateTime? installedOn = null;
                var installedOnStr = obj["InstalledOn"]?.ToString();
                if (!string.IsNullOrEmpty(installedOnStr))
                {
                    if (DateTime.TryParse(installedOnStr, out var dt))
                        installedOn = dt.ToUniversalTime();
                }

                patches.Add(new InstalledPatch
                {
                    KbNumber = kbNumber,
                    Title = obj["Description"]?.ToString(),
                    InstalledOn = installedOn
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to collect installed patches via WMI");
        }

        _logger.LogInformation("Collected {Count} installed patches", patches.Count);
        return patches;
    }

    public List<PendingPatch> CollectPendingPatches()
    {
        var pending = new List<PendingPatch>();

        try
        {
            // Use WUApiLib COM interop to search for pending updates
            var updateSessionType = Type.GetTypeFromProgID("Microsoft.Update.Session");
            if (updateSessionType == null)
            {
                _logger.LogWarning("Windows Update API (Microsoft.Update.Session) not available");
                return pending;
            }

            dynamic updateSession = Activator.CreateInstance(updateSessionType)!;
            dynamic updateSearcher = updateSession.CreateUpdateSearcher();

            _logger.LogDebug("Searching for pending Windows updates...");
            dynamic searchResult = updateSearcher.Search("IsInstalled=0");

            foreach (dynamic update in searchResult.Updates)
            {
                try
                {
                    string? kbNumber = null;

                    // Get KB article IDs
                    dynamic? kbIds = update.KBArticleIDs;
                    if (kbIds != null && (int)kbIds.Count > 0)
                    {
                        kbNumber = "KB" + ((object)kbIds.Item(0)).ToString();
                    }

                    if (string.IsNullOrEmpty(kbNumber))
                    {
                        // Some updates don't have KB numbers, use the update ID
                        kbNumber = update.Identity?.UpdateID?.ToString() ?? "Unknown";
                    }

                    string? severity = null;
                    try
                    {
                        severity = update.MsrcSeverity?.ToString()?.ToLower();
                    }
                    catch { }

                    string? category = null;
                    try
                    {
                        dynamic? categories = update.Categories;
                        if (categories != null && (int)categories.Count > 0)
                        {
                            category = MapCategory(((object?)categories.Item(0).Name)?.ToString());
                        }
                    }
                    catch { }

                    pending.Add(new PendingPatch
                    {
                        KbNumber = kbNumber,
                        Title = update.Title?.ToString(),
                        Severity = severity,
                        Category = category
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to read a pending update entry");
                }
            }

            // Release COM objects
            Marshal.ReleaseComObject(searchResult);
            Marshal.ReleaseComObject(updateSearcher);
            Marshal.ReleaseComObject(updateSession);
        }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x80240024))
        {
            // WU_E_NO_SERVICE - Windows Update service not running
            _logger.LogWarning("Windows Update service is not running");
        }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x8024001E))
        {
            // WU_E_SERVICE_STOP - Service being stopped
            _logger.LogWarning("Windows Update service is stopping");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect pending patches via WUApi");
        }

        _logger.LogInformation("Collected {Count} pending patches", pending.Count);
        return pending;
    }

    private static string MapCategory(string? categoryName)
    {
        if (string.IsNullOrEmpty(categoryName)) return "other";

        return categoryName.ToLower() switch
        {
            var c when c.Contains("critical") => "critical_updates",
            var c when c.Contains("security") => "critical_updates",
            var c when c.Contains("rollup") => "update_rollups",
            var c when c.Contains("service pack") => "service_packs",
            var c when c.Contains("feature") => "feature_packs",
            var c when c.Contains("definition") => "definition_packs",
            var c when c.Contains("driver") => "drivers",
            _ => "other"
        };
    }
}
