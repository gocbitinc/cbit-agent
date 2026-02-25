#pragma warning disable CS8602 // COM interop dynamic member access
using System.Runtime.InteropServices;
using System.Text.Json;
using CbitAgent.Models;
using Microsoft.Extensions.Logging;

namespace CbitAgent.Services;

/// <summary>
/// Scans for, downloads, and installs Windows Updates via WUApiLib COM interop.
/// Thread-safe: only one update operation runs at a time via SemaphoreSlim.
/// Supports ad-hoc KB installs and policy-based update runs.
/// </summary>
public class WindowsUpdateExecutor
{
    private readonly ILogger<WindowsUpdateExecutor> _logger;
    private readonly SemaphoreSlim _executionLock = new(1, 1);

    public WindowsUpdateExecutor(ILogger<WindowsUpdateExecutor> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Scans for available (not-installed) Windows Updates.
    /// Returns the same PendingPatch model used by PatchInfoCollector.
    /// </summary>
    public async Task<List<PendingPatch>> ScanAsync(CancellationToken ct = default)
    {
        return await Task.Run(() => ScanForUpdates(), ct);
    }

    /// <summary>
    /// Downloads and installs specific KBs. Reports progress via optional callback.
    /// Only one install operation can run at a time.
    /// </summary>
    public async Task<UpdateJobResult> InstallUpdatesAsync(
        List<string> kbNumbers,
        string? rebootBehavior,
        Func<UpdateProgress, Task>? onProgress,
        CancellationToken ct)
    {
        if (!await _executionLock.WaitAsync(0, ct))
        {
            return new UpdateJobResult
            {
                Status = "failed",
                ErrorMessage = "Another update operation is already running"
            };
        }

        try
        {
            return await Task.Run(() =>
                InstallUpdatesCore(kbNumbers, rebootBehavior, onProgress, ct), ct);
        }
        catch (OperationCanceledException)
        {
            return new UpdateJobResult
            {
                Status = "failed",
                ErrorMessage = "Operation was cancelled"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during update installation");
            return new UpdateJobResult
            {
                Status = "failed",
                ErrorMessage = ex.Message
            };
        }
        finally
        {
            _executionLock.Release();
        }
    }

    /// <summary>
    /// Runs policy-based updates: scans, filters by severity/category approval
    /// rules and KB exclusions, then downloads and installs approved updates.
    /// </summary>
    public async Task<UpdateJobResult> RunPolicyUpdatesAsync(
        JsonElement? policy,
        string? policyId,
        Func<UpdateProgress, Task>? onProgress,
        CancellationToken ct)
    {
        if (policy == null || policy.Value.ValueKind == JsonValueKind.Undefined)
        {
            return new UpdateJobResult
            {
                Status = "failed",
                ErrorMessage = "No policy provided"
            };
        }

        if (!await _executionLock.WaitAsync(0, ct))
        {
            return new UpdateJobResult
            {
                Status = "failed",
                ErrorMessage = "Another update operation is already running"
            };
        }

        try
        {
            return await Task.Run(() =>
                RunPolicyUpdatesCore(policy.Value, onProgress, ct), ct);
        }
        catch (OperationCanceledException)
        {
            return new UpdateJobResult
            {
                Status = "failed",
                ErrorMessage = "Operation was cancelled"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during policy update run");
            return new UpdateJobResult
            {
                Status = "failed",
                ErrorMessage = ex.Message
            };
        }
        finally
        {
            _executionLock.Release();
        }
    }

    /// <summary>
    /// Checks if a reboot is pending from a previous update installation.
    /// </summary>
    public bool CheckRebootRequired()
    {
        try
        {
            var sysInfoType = Type.GetTypeFromProgID("Microsoft.Update.SystemInfo");
            if (sysInfoType == null) return false;

            dynamic sysInfo = Activator.CreateInstance(sysInfoType)!;
            bool required = sysInfo.RebootRequired;
            Marshal.ReleaseComObject(sysInfo);
            return required;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check reboot status");
            return false;
        }
    }

    // ─── Core implementation ───────────────────────────────────────────

    private List<PendingPatch> ScanForUpdates()
    {
        var pending = new List<PendingPatch>();
        dynamic? updateSession = null;
        dynamic? updateSearcher = null;
        dynamic? searchResult = null;

        try
        {
            var sessionType = Type.GetTypeFromProgID("Microsoft.Update.Session");
            if (sessionType == null)
            {
                _logger.LogWarning("Microsoft.Update.Session not available");
                return pending;
            }

            updateSession = Activator.CreateInstance(sessionType)!;
            updateSearcher = updateSession.CreateUpdateSearcher();

            _logger.LogInformation("Scanning for available Windows Updates...");
            searchResult = updateSearcher.Search("IsInstalled=0");

            int count = (int)searchResult.Updates.Count;
            _logger.LogInformation("Scan found {Count} available updates", count);

            for (int i = 0; i < count; i++)
            {
                dynamic update = searchResult.Updates.Item(i);
                try
                {
                    pending.Add(ExtractPatchInfo(update));
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to read update at index {Index}", i);
                }
            }
        }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x80240024))
        {
            _logger.LogWarning("Windows Update service is not running");
        }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x8024001E))
        {
            _logger.LogWarning("Windows Update service is stopping");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Windows Update scan failed");
        }
        finally
        {
            if (searchResult != null) Marshal.ReleaseComObject(searchResult);
            if (updateSearcher != null) Marshal.ReleaseComObject(updateSearcher);
            if (updateSession != null) Marshal.ReleaseComObject(updateSession);
        }

        return pending;
    }

    private UpdateJobResult InstallUpdatesCore(
        List<string> kbNumbers,
        string? rebootBehavior,
        Func<UpdateProgress, Task>? onProgress,
        CancellationToken ct)
    {
        var result = new UpdateJobResult();
        dynamic? updateSession = null;
        dynamic? updateSearcher = null;
        dynamic? searchResult = null;
        dynamic? updatesToInstall = null;
        dynamic? downloader = null;
        dynamic? installer = null;

        try
        {
            // Normalize KB numbers — accept with or without "KB" prefix
            var normalizedKbs = kbNumbers
                .Select(kb => kb.StartsWith("KB", StringComparison.OrdinalIgnoreCase)
                    ? kb.Substring(2) : kb)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            _logger.LogInformation("Installing updates for KBs: {Kbs}",
                string.Join(", ", kbNumbers));

            ReportProgress(onProgress, "scanning", null, 0);

            var sessionType = Type.GetTypeFromProgID("Microsoft.Update.Session");
            if (sessionType == null)
            {
                result.Status = "failed";
                result.ErrorMessage = "Microsoft.Update.Session not available";
                return result;
            }

            updateSession = Activator.CreateInstance(sessionType)!;
            updateSearcher = updateSession.CreateUpdateSearcher();

            ct.ThrowIfCancellationRequested();

            searchResult = updateSearcher.Search("IsInstalled=0");
            int totalAvailable = (int)searchResult.Updates.Count;

            _logger.LogInformation("Scan found {Count} available updates, filtering to requested KBs",
                totalAvailable);

            // Build collection of matching updates
            var collType = Type.GetTypeFromProgID("Microsoft.Update.UpdateColl")!;
            updatesToInstall = Activator.CreateInstance(collType)!;

            var matchedKbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < totalAvailable; i++)
            {
                dynamic update = searchResult.Updates.Item(i);
                string? kbNum = GetKbNumber(update);

                if (kbNum != null && normalizedKbs.Contains(kbNum))
                {
                    // Accept EULA if needed
                    if (!update.EulaAccepted)
                        update.AcceptEula();

                    updatesToInstall.Add(update);
                    matchedKbs.Add(kbNum);
                    _logger.LogInformation("Matched update: KB{Kb} — {Title}", kbNum, (string)update.Title);
                }
            }

            int matchCount = (int)updatesToInstall.Count;
            if (matchCount == 0)
            {
                // All requested KBs not found — report them as failed
                result.Status = "failed";
                result.KbsFailed = kbNumbers.ToList();
                result.ErrorMessage = "None of the requested updates were found available";
                result.RebootRequired = CheckRebootRequired();
                return result;
            }

            ct.ThrowIfCancellationRequested();

            // ── Download ──
            ReportProgress(onProgress, "downloading", null, 10);
            _logger.LogInformation("Downloading {Count} updates...", matchCount);

            downloader = updateSession.CreateUpdateDownloader();
            downloader.Updates = updatesToInstall;
            dynamic downloadResult = downloader.Download();

            int dlResultCode = (int)downloadResult.ResultCode;
            _logger.LogInformation("Download result code: {Code}", dlResultCode);
            Marshal.ReleaseComObject(downloadResult);

            if (dlResultCode == 4 || dlResultCode == 5) // Failed or Aborted
            {
                result.Status = "failed";
                result.KbsFailed = kbNumbers.ToList();
                result.ErrorMessage = $"Download failed with result code {dlResultCode}";
                result.RebootRequired = CheckRebootRequired();
                return result;
            }

            ct.ThrowIfCancellationRequested();

            // ── Install ──
            ReportProgress(onProgress, "installing", null, 50);
            _logger.LogInformation("Installing {Count} updates...", matchCount);

            installer = updateSession.CreateUpdateInstaller();
            installer.Updates = updatesToInstall;
            dynamic installResult = installer.Install();

            // Process per-update results
            for (int i = 0; i < matchCount; i++)
            {
                dynamic update = updatesToInstall.Item(i);
                string? kbNum = GetKbNumber(update);
                string kbLabel = kbNum != null ? $"KB{kbNum}" : "Unknown";

                dynamic updateResult = installResult.GetUpdateResult(i);
                int updateResultCode = (int)updateResult.ResultCode;
                Marshal.ReleaseComObject(updateResult);

                if (updateResultCode == 2) // Succeeded
                {
                    result.KbsInstalled.Add(kbLabel);
                    _logger.LogInformation("Installed: {Kb}", kbLabel);
                }
                else
                {
                    result.KbsFailed.Add(kbLabel);
                    _logger.LogWarning("Failed to install {Kb}: result code {Code}",
                        kbLabel, updateResultCode);
                }

                int pct = 50 + (int)(((i + 1) / (float)matchCount) * 45);
                ReportProgress(onProgress, "installing", kbLabel, pct);
            }

            Marshal.ReleaseComObject(installResult);

            result.RebootRequired = CheckRebootRequired();
            result.Status = result.KbsFailed.Count == 0 ? "success" : "failed";

            if (result.KbsFailed.Count > 0 && result.KbsInstalled.Count > 0)
                result.Status = "partial";

            // Report any requested KBs that weren't found at all
            foreach (var kb in kbNumbers)
            {
                var num = kb.StartsWith("KB", StringComparison.OrdinalIgnoreCase)
                    ? kb.Substring(2) : kb;
                if (!matchedKbs.Contains(num))
                {
                    var label = num.StartsWith("KB", StringComparison.OrdinalIgnoreCase)
                        ? kb : $"KB{num}";
                    if (!result.KbsFailed.Contains(label))
                        result.KbsFailed.Add(label);
                }
            }

            ReportProgress(onProgress, result.Status, null, 100);
            _logger.LogInformation(
                "Update job complete: {Installed} installed, {Failed} failed, reboot={Reboot}",
                result.KbsInstalled.Count, result.KbsFailed.Count, result.RebootRequired);

            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update installation failed");
            result.Status = "failed";
            result.ErrorMessage = ex.Message;
            result.KbsFailed = kbNumbers.ToList();
            result.RebootRequired = CheckRebootRequired();
            return result;
        }
        finally
        {
            if (installer != null) try { Marshal.ReleaseComObject(installer); } catch { }
            if (downloader != null) try { Marshal.ReleaseComObject(downloader); } catch { }
            if (updatesToInstall != null) try { Marshal.ReleaseComObject(updatesToInstall); } catch { }
            if (searchResult != null) try { Marshal.ReleaseComObject(searchResult); } catch { }
            if (updateSearcher != null) try { Marshal.ReleaseComObject(updateSearcher); } catch { }
            if (updateSession != null) try { Marshal.ReleaseComObject(updateSession); } catch { }
        }
    }

    private UpdateJobResult RunPolicyUpdatesCore(
        JsonElement policy,
        Func<UpdateProgress, Task>? onProgress,
        CancellationToken ct)
    {
        var result = new UpdateJobResult();
        dynamic? updateSession = null;
        dynamic? updateSearcher = null;
        dynamic? searchResult = null;
        dynamic? updatesToInstall = null;
        dynamic? downloader = null;
        dynamic? installer = null;

        try
        {
            // Parse policy
            var excludedKbs = GetPolicyStringList(policy, "excluded_kbs");

            ReportProgress(onProgress, "scanning", null, 0);
            _logger.LogInformation("Running policy-based update scan...");

            var sessionType = Type.GetTypeFromProgID("Microsoft.Update.Session");
            if (sessionType == null)
            {
                result.Status = "failed";
                result.ErrorMessage = "Microsoft.Update.Session not available";
                return result;
            }

            updateSession = Activator.CreateInstance(sessionType)!;
            updateSearcher = updateSession.CreateUpdateSearcher();

            ct.ThrowIfCancellationRequested();

            searchResult = updateSearcher.Search("IsInstalled=0");
            int totalAvailable = (int)searchResult.Updates.Count;

            _logger.LogInformation("Policy scan found {Count} available updates, applying policy filter",
                totalAvailable);

            var collType = Type.GetTypeFromProgID("Microsoft.Update.UpdateColl")!;
            updatesToInstall = Activator.CreateInstance(collType)!;

            for (int i = 0; i < totalAvailable; i++)
            {
                dynamic update = searchResult.Updates.Item(i);

                try
                {
                    string? kbNum = GetKbNumber(update);

                    // Check KB exclusions
                    if (kbNum != null && excludedKbs.Any(ex =>
                        ex.Replace("KB", "").Equals(kbNum, StringComparison.OrdinalIgnoreCase)))
                    {
                        _logger.LogDebug("Excluded by policy: KB{Kb}", kbNum);
                        continue;
                    }

                    // Check severity approval
                    string severity = GetUpdateSeverity(update);
                    string severityRule = GetPolicyString(policy, $"severity_{severity}") ?? "manual";

                    if (severityRule != "approve")
                    {
                        _logger.LogDebug("Severity {Severity} not approved (rule={Rule}): KB{Kb}",
                            severity, severityRule, kbNum);
                        continue;
                    }

                    // Check category approval
                    string category = GetUpdateCategory(update);
                    string categoryRule = GetPolicyString(policy, $"category_{category}") ?? "manual";

                    if (categoryRule != "approve")
                    {
                        _logger.LogDebug("Category {Category} not approved (rule={Rule}): KB{Kb}",
                            category, categoryRule, kbNum);
                        continue;
                    }

                    // Both severity and category approved
                    if (!update.EulaAccepted)
                        update.AcceptEula();

                    updatesToInstall.Add(update);
                    _logger.LogInformation("Policy approved: KB{Kb} — {Title} (sev={Severity}, cat={Category})",
                        kbNum, (string)update.Title, severity, category);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error evaluating update at index {Index}", i);
                }
            }

            int matchCount = (int)updatesToInstall.Count;
            if (matchCount == 0)
            {
                result.Status = "success";
                result.RebootRequired = CheckRebootRequired();
                _logger.LogInformation("No updates matched the policy criteria");
                ReportProgress(onProgress, "success", null, 100);
                return result;
            }

            ct.ThrowIfCancellationRequested();

            // ── Download ──
            ReportProgress(onProgress, "downloading", null, 10);
            _logger.LogInformation("Downloading {Count} policy-approved updates...", matchCount);

            downloader = updateSession.CreateUpdateDownloader();
            downloader.Updates = updatesToInstall;
            dynamic downloadResult = downloader.Download();

            int dlResultCode = (int)downloadResult.ResultCode;
            _logger.LogInformation("Download result code: {Code}", dlResultCode);
            Marshal.ReleaseComObject(downloadResult);

            if (dlResultCode == 4 || dlResultCode == 5)
            {
                result.Status = "failed";
                result.ErrorMessage = $"Download failed with result code {dlResultCode}";
                result.RebootRequired = CheckRebootRequired();
                return result;
            }

            ct.ThrowIfCancellationRequested();

            // ── Install ──
            ReportProgress(onProgress, "installing", null, 50);
            _logger.LogInformation("Installing {Count} updates...", matchCount);

            installer = updateSession.CreateUpdateInstaller();
            installer.Updates = updatesToInstall;
            dynamic installResult = installer.Install();

            for (int i = 0; i < matchCount; i++)
            {
                dynamic update = updatesToInstall.Item(i);
                string? kbNum = GetKbNumber(update);
                string kbLabel = kbNum != null ? $"KB{kbNum}" : "Unknown";

                dynamic updateResult = installResult.GetUpdateResult(i);
                int updateResultCode = (int)updateResult.ResultCode;
                Marshal.ReleaseComObject(updateResult);

                if (updateResultCode == 2)
                {
                    result.KbsInstalled.Add(kbLabel);
                    _logger.LogInformation("Installed: {Kb}", kbLabel);
                }
                else
                {
                    result.KbsFailed.Add(kbLabel);
                    _logger.LogWarning("Failed to install {Kb}: result code {Code}",
                        kbLabel, updateResultCode);
                }

                int pct = 50 + (int)(((i + 1) / (float)matchCount) * 45);
                ReportProgress(onProgress, "installing", kbLabel, pct);
            }

            Marshal.ReleaseComObject(installResult);

            result.RebootRequired = CheckRebootRequired();
            result.Status = result.KbsFailed.Count == 0 ? "success" : "failed";

            if (result.KbsFailed.Count > 0 && result.KbsInstalled.Count > 0)
                result.Status = "partial";

            ReportProgress(onProgress, result.Status, null, 100);
            _logger.LogInformation(
                "Policy update job complete: {Installed} installed, {Failed} failed, reboot={Reboot}",
                result.KbsInstalled.Count, result.KbsFailed.Count, result.RebootRequired);

            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Policy update run failed");
            result.Status = "failed";
            result.ErrorMessage = ex.Message;
            result.RebootRequired = CheckRebootRequired();
            return result;
        }
        finally
        {
            if (installer != null) try { Marshal.ReleaseComObject(installer); } catch { }
            if (downloader != null) try { Marshal.ReleaseComObject(downloader); } catch { }
            if (updatesToInstall != null) try { Marshal.ReleaseComObject(updatesToInstall); } catch { }
            if (searchResult != null) try { Marshal.ReleaseComObject(searchResult); } catch { }
            if (updateSearcher != null) try { Marshal.ReleaseComObject(updateSearcher); } catch { }
            if (updateSession != null) try { Marshal.ReleaseComObject(updateSession); } catch { }
        }
    }

    // ─── Helpers ───────────────────────────────────────────────────────

    private static string? GetKbNumber(dynamic update)
    {
        try
        {
            dynamic? kbIds = update.KBArticleIDs;
            if (kbIds != null && (int)kbIds.Count > 0)
                return ((object)kbIds.Item(0)).ToString();
        }
        catch { }
        return null;
    }

    private PendingPatch ExtractPatchInfo(dynamic update)
    {
        string? kbNum = GetKbNumber(update);
        string kbLabel = kbNum != null ? $"KB{kbNum}" : (update.Identity?.UpdateID?.ToString() ?? "Unknown");

        string? severity = null;
        try { severity = update.MsrcSeverity?.ToString()?.ToLower(); } catch { }

        string? category = null;
        try
        {
            dynamic? categories = update.Categories;
            if (categories != null && (int)categories.Count > 0)
                category = MapCategory(((object?)categories.Item(0).Name)?.ToString());
        }
        catch { }

        return new PendingPatch
        {
            KbNumber = kbLabel,
            Title = update.Title?.ToString(),
            Severity = severity,
            Category = category
        };
    }

    private static string GetUpdateSeverity(dynamic update)
    {
        try
        {
            string? sev = update.MsrcSeverity?.ToString()?.ToLower();
            return sev switch
            {
                "critical" => "critical",
                "important" => "important",
                "moderate" => "moderate",
                "low" => "low",
                _ => "other"
            };
        }
        catch
        {
            return "other";
        }
    }

    private static string GetUpdateCategory(dynamic update)
    {
        try
        {
            dynamic? categories = update.Categories;
            if (categories != null && (int)categories.Count > 0)
                return MapCategory(((object?)categories.Item(0).Name)?.ToString());
        }
        catch { }
        return "other";
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

    private static void ReportProgress(Func<UpdateProgress, Task>? onProgress, string status,
        string? currentKb, int percent)
    {
        if (onProgress == null) return;
        _ = onProgress(new UpdateProgress
        {
            Status = status,
            CurrentKb = currentKb,
            ProgressPercent = percent
        });
    }

    // ─── Policy JSON helpers ───────────────────────────────────────────

    private static string? GetPolicyString(JsonElement policy, string key)
    {
        if (policy.TryGetProperty(key, out var val) && val.ValueKind == JsonValueKind.String)
            return val.GetString();
        return null;
    }

    private static List<string> GetPolicyStringList(JsonElement policy, string key)
    {
        var list = new List<string>();
        if (policy.TryGetProperty(key, out var val) && val.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in val.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                    list.Add(item.GetString()!);
            }
        }
        return list;
    }
}
