using System.Management;
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

                    disks.Add(new DiskInfo
                    {
                        DriveLetter = drive.Name.TrimEnd('\\'),
                        Label = drive.VolumeLabel ?? string.Empty,
                        TotalGb = totalGb,
                        UsedGb = usedGb,
                        FreeGb = freeGb,
                        FileSystem = drive.DriveFormat ?? string.Empty
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
                        Status = predictFailure ? "warning" : "healthy",
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
