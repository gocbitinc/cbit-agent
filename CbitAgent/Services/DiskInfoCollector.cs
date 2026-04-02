using System.Management;
using System.Text.RegularExpressions;
using CbitAgent.Models;
using Microsoft.Extensions.Logging;

namespace CbitAgent.Services;

public class DiskInfoCollector
{
    private readonly ILogger<DiskInfoCollector> _logger;

    public DiskInfoCollector(ILogger<DiskInfoCollector> logger)
    {
        _logger = logger;
    }

    public List<DiskInfo> CollectDisks()
    {
        var disks = new List<DiskInfo>();

        // Build drive-letter → smart_status map before iterating drives.
        // Uses WMI SMART prediction + partition associations; falls back to "unknown" on any failure.
        var smartStatusByDrive = BuildDriveSmartStatusMap();

        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
                    continue;

                try
                {
                    var totalGb = Math.Round(drive.TotalSize / (1024.0 * 1024 * 1024), 2);
                    var freeGb = Math.Round(drive.AvailableFreeSpace / (1024.0 * 1024 * 1024), 2);
                    var usedGb = Math.Round(totalGb - freeGb, 2);
                    var driveLetter = drive.Name.TrimEnd('\\');

                    // Look up the SMART status for this drive letter.
                    // "unknown" when the physical disk cannot be identified or SMART is unavailable.
                    smartStatusByDrive.TryGetValue(driveLetter, out var smartStatus);

                    disks.Add(new DiskInfo
                    {
                        DriveLetter = driveLetter,
                        Label = drive.VolumeLabel ?? string.Empty,
                        TotalGb = totalGb,
                        UsedGb = usedGb,
                        FreeGb = freeGb,
                        FileSystem = drive.DriveFormat ?? string.Empty,
                        SmartStatus = smartStatus ?? "unknown"
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to read drive {Name}", drive.Name);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enumerate drives");
        }

        _logger.LogInformation("Collected {Count} fixed disks", disks.Count);
        return disks;
    }

    /// <summary>
    /// Builds a mapping of drive letter → smart_status by correlating:
    ///   MSStorageDriver_FailurePredictStatus (disk index → predict_failure)
    ///   Win32_DiskDrive → Win32_DiskPartition → Win32_LogicalDisk (disk index → drive letter)
    ///
    /// Returns an empty dictionary when SMART is unavailable; callers default to "unknown".
    /// Values: "critical" (predict_failure=true), "healthy" (predict_failure=false).
    /// </summary>
    private Dictionary<string, string> BuildDriveSmartStatusMap()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            // Step 1 — disk index → predict_failure from SMART WMI namespace
            var predictByIndex = new Dictionary<int, bool>();
            try
            {
                using var smartSearcher = new ManagementObjectSearcher(
                    @"root\WMI",
                    "SELECT InstanceName, PredictFailure FROM MSStorageDriver_FailurePredictStatus");

                var fallbackIdx = 0;
                foreach (ManagementObject obj in smartSearcher.Get())
                {
                    var predict = Convert.ToBoolean(obj["PredictFailure"]);
                    var instanceName = obj["InstanceName"]?.ToString() ?? string.Empty;

                    // Prefer extracting disk index from InstanceName (e.g. "\Device\Harddisk0\DR0")
                    // which directly matches Win32_DiskDrive.Index.
                    var match = Regex.Match(instanceName, @"Harddisk(\d+)", RegexOptions.IgnoreCase);
                    var diskIndex = match.Success && int.TryParse(match.Groups[1].Value, out var parsed)
                        ? parsed
                        : fallbackIdx;

                    predictByIndex[diskIndex] = predict;
                    fallbackIdx++;
                }
            }
            catch (ManagementException)
            {
                // SMART WMI class not available on this system — return empty map
                _logger.LogDebug("MSStorageDriver_FailurePredictStatus not available — smart_status will be 'unknown'");
                return result;
            }

            // Step 2 — walk Win32_DiskDrive → partitions → logical disks to build drive letter → disk index
            using var diskSearcher = new ManagementObjectSearcher(
                "SELECT DeviceID, Index FROM Win32_DiskDrive");

            foreach (ManagementObject disk in diskSearcher.Get())
            {
                var diskIndex = Convert.ToInt32(disk["Index"]);
                var deviceId = disk["DeviceID"]?.ToString() ?? string.Empty;

                // WQL ASSOCIATORS path: backslashes in DeviceID are literal (no extra escaping needed in WQL strings)
                using var partSearcher = new ManagementObjectSearcher(
                    $"ASSOCIATORS OF {{Win32_DiskDrive.DeviceID=\"{deviceId}\"}} " +
                    "WHERE AssocClass=Win32_DiskDriveToDiskPartition");

                foreach (ManagementObject partition in partSearcher.Get())
                {
                    var partDeviceId = partition["DeviceID"]?.ToString() ?? string.Empty;

                    using var logSearcher = new ManagementObjectSearcher(
                        $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID=\"{partDeviceId}\"}} " +
                        "WHERE AssocClass=Win32_LogicalDiskToPartition");

                    foreach (ManagementObject logical in logSearcher.Get())
                    {
                        var driveLetter = logical["DeviceID"]?.ToString()?.TrimEnd('\\');
                        if (string.IsNullOrEmpty(driveLetter)) continue;

                        if (predictByIndex.TryGetValue(diskIndex, out var predict))
                            result[driveLetter] = predict ? "critical" : "healthy";
                        else
                            result[driveLetter] = "unknown";
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to build drive smart status map — smart_status will be 'unknown' for all drives");
        }

        return result;
    }

    public List<SmartData> CollectSmartData()
    {
        var smartList = new List<SmartData>();

        try
        {
            // Get disk models from Win32_DiskDrive
            var diskModels = new Dictionary<int, string>();
            using (var searcher = new ManagementObjectSearcher("SELECT Index, Model FROM Win32_DiskDrive"))
            {
                foreach (var obj in searcher.Get())
                {
                    var index = Convert.ToInt32(obj["Index"]);
                    var model = obj["Model"]?.ToString() ?? $"Disk {index}";
                    diskModels[index] = model;
                }
            }

            // Try SMART failure prediction
            try
            {
                using var smartSearcher = new ManagementObjectSearcher(
                    @"root\WMI",
                    "SELECT InstanceName, PredictFailure FROM MSStorageDriver_FailurePredictStatus");

                int diskIndex = 0;
                foreach (var obj in smartSearcher.Get())
                {
                    var predictFailure = Convert.ToBoolean(obj["PredictFailure"]);
                    var instanceName = obj["InstanceName"]?.ToString() ?? string.Empty;

                    var diskId = diskModels.TryGetValue(diskIndex, out var model)
                        ? model
                        : instanceName;

                    smartList.Add(new SmartData
                    {
                        DiskIdentifier = diskId,
                        Status = predictFailure ? "critical" : "healthy",
                        Attributes = new Dictionary<string, object>
                        {
                            ["predict_failure"] = predictFailure,
                            ["instance_name"] = instanceName
                        }
                    });

                    diskIndex++;
                }
            }
            catch (ManagementException ex)
            {
                _logger.LogDebug(ex, "SMART failure prediction not available (WMI class not found)");

                // Fall back: report each disk as unknown
                foreach (var (index, model) in diskModels)
                {
                    smartList.Add(new SmartData
                    {
                        DiskIdentifier = model,
                        Status = "unknown",
                        Attributes = new Dictionary<string, object>()
                    });
                }
            }

            // Try to get SMART threshold data for additional attributes
            try
            {
                using var thresholdSearcher = new ManagementObjectSearcher(
                    @"root\WMI",
                    "SELECT * FROM MSStorageDriver_FailurePredictThresholds");

                int idx = 0;
                foreach (var obj in thresholdSearcher.Get())
                {
                    if (idx < smartList.Count)
                    {
                        var thresholdData = obj["VendorSpecific"] as byte[];
                        if (thresholdData != null)
                        {
                            smartList[idx].Attributes["threshold_data_length"] = thresholdData.Length;
                        }
                    }
                    idx++;
                }
            }
            catch
            {
                // Threshold data not available on all systems
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect SMART data");
        }

        _logger.LogInformation("Collected SMART data for {Count} disks", smartList.Count);
        return smartList;
    }
}
