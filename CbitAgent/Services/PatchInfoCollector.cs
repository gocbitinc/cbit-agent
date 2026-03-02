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
        var seenKbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var sessionType = Type.GetTypeFromProgID("Microsoft.Update.Session");
            if (sessionType == null)
            {
                _logger.LogWarning("Microsoft.Update.Session not available, falling back to WMI");
                return CollectInstalledPatchesWmi();
            }

            dynamic updateSession = Activator.CreateInstance(sessionType)!;
            dynamic updateSearcher = updateSession.CreateUpdateSearcher();

            int totalCount = updateSearcher.GetTotalHistoryCount();
            _logger.LogInformation("Windows Update history contains {Count} entries", totalCount);

            if (totalCount == 0)
            {
                Marshal.ReleaseComObject(updateSearcher);
                Marshal.ReleaseComObject(updateSession);
                return patches;
            }

            dynamic historyEntries = updateSearcher.QueryHistory(0, totalCount);
            int entryCount = (int)historyEntries.Count;

            for (int i = 0; i < entryCount; i++)
            {
                try
                {
                    dynamic entry = historyEntries.Item(i);

                    // Only include successfully installed updates
                    // ResultCode: 2 = Succeeded, 3 = Succeeded with errors
                    int resultCode = (int)entry.ResultCode;
                    if (resultCode != 2 && resultCode != 3)
                        continue;

                    // Operation: 1 = Installation (skip uninstalls which are 2)
                    int operation = (int)entry.Operation;
                    if (operation != 1)
                        continue;

                    string? title = entry.Title?.ToString();
                    string? kbNumber = ExtractKbFromTitle(title);

                    // Try UpdateIdentity if title didn't yield a KB
                    if (string.IsNullOrEmpty(kbNumber))
                    {
                        try
                        {
                            string? updateId = entry.UpdateIdentity?.UpdateID?.ToString();
                            if (!string.IsNullOrEmpty(updateId))
                                kbNumber = updateId;
                        }
                        catch { }
                    }

                    if (string.IsNullOrEmpty(kbNumber))
                        continue;

                    // Deduplicate — keep the most recent entry for each KB
                    if (seenKbs.Contains(kbNumber))
                        continue;
                    seenKbs.Add(kbNumber);

                    DateTime? installedOn = null;
                    try
                    {
                        DateTime dt = (DateTime)entry.Date;
                        if (dt > DateTime.MinValue)
                            installedOn = dt.ToUniversalTime();
                    }
                    catch { }

                    // Infer category from title (history entries don't have Categories collection)
                    string? category = InferCategoryFromTitle(title);

                    // Only prefix "KB" for actual KB numbers, not GUIDs
                    string displayKb = kbNumber;
                    if (!kbNumber.StartsWith("KB", StringComparison.OrdinalIgnoreCase))
                    {
                        // Check if it's a numeric KB (not a GUID)
                        if (kbNumber.All(char.IsDigit))
                            displayKb = $"KB{kbNumber}";
                        // else leave as-is (GUID-based identifier for driver updates etc.)
                    }

                    patches.Add(new InstalledPatch
                    {
                        KbNumber = displayKb,
                        Title = title,
                        InstalledOn = installedOn,
                        Category = category
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to read update history entry at index {Index}", i);
                }
            }

            Marshal.ReleaseComObject(historyEntries);
            Marshal.ReleaseComObject(updateSearcher);
            Marshal.ReleaseComObject(updateSession);
        }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x80240024))
        {
            _logger.LogWarning("Windows Update service is not running, falling back to WMI");
            return CollectInstalledPatchesWmi();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to collect installed patches via WU API, falling back to WMI");
            return CollectInstalledPatchesWmi();
        }

        _logger.LogInformation("Collected {Count} installed patches via Windows Update API", patches.Count);
        return patches;
    }

    /// <summary>
    /// Extracts KB number from update title, e.g. "2024-01 Cumulative Update (KB5034441)" → "KB5034441"
    /// </summary>
    private static string? ExtractKbFromTitle(string? title)
    {
        if (string.IsNullOrEmpty(title)) return null;

        var match = System.Text.RegularExpressions.Regex.Match(title, @"KB(\d+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? $"KB{match.Groups[1].Value}" : null;
    }

    /// <summary>
    /// Fallback: collect installed patches via WMI Win32_QuickFixEngineering (limited subset).
    /// </summary>
    private List<InstalledPatch> CollectInstalledPatchesWmi()
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

        _logger.LogInformation("Collected {Count} installed patches via WMI (fallback)", patches.Count);
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

    /// <summary>
    /// Infers update category from the title string (history entries lack a Categories collection).
    /// </summary>
    private static string? InferCategoryFromTitle(string? title)
    {
        if (string.IsNullOrEmpty(title)) return null;

        var t = title.ToLower();
        return t switch
        {
            _ when t.Contains("cumulative update") => "update_rollups",
            _ when t.Contains("security intelligence") || t.Contains("definition update") => "definition_packs",
            _ when t.Contains("security update") => "critical_updates",
            _ when t.Contains("servicing stack") => "critical_updates",
            _ when t.Contains("driver update") || t.Contains("- display -") || t.Contains("- monitor -")
                || t.Contains("- net -") || t.Contains("- wdc_") || t.Contains("- usb") => "drivers",
            _ when t.Contains("feature update") || t.Contains("feature experience") => "feature_packs",
            _ when t.Contains(".net framework") || t.Contains(".net core") => "other",
            _ when t.Contains("malicious software removal") => "critical_updates",
            _ => null
        };
    }
}
