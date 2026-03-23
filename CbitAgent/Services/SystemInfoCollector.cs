using System.Diagnostics;
using System.DirectoryServices;
using System.Management;
using CbitAgent.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace CbitAgent.Services;

public class SystemInfoCollector
{
    private readonly ILogger<SystemInfoCollector> _logger;

    public SystemInfoCollector(ILogger<SystemInfoCollector> logger)
    {
        _logger = logger;
    }

    public SystemInfo Collect()
    {
        var info = new SystemInfo
        {
            Hostname = Environment.MachineName
        };

        try
        {
            CollectComputerSystem(info);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect Win32_ComputerSystem data");
        }

        try
        {
            CollectOperatingSystem(info);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect Win32_OperatingSystem data");
        }

        try
        {
            CollectProcessor(info);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect Win32_Processor data");
        }

        try
        {
            CollectBios(info);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect Win32_BIOS data");
        }

        if (info.LastBootTime.HasValue)
        {
            info.UptimeSeconds = (long)(DateTime.UtcNow - info.LastBootTime.Value).TotalSeconds;
        }
        else
        {
            info.UptimeSeconds = Environment.TickCount64 / 1000;
        }

        _logger.LogInformation("System info collected for {Hostname} ({OsType})", info.Hostname, info.OsType);
        _logger.LogDebug(
            "System info details:\n" +
            "  Hostname:      {Hostname}\n" +
            "  OS:            {OsVersion} (Build {OsBuild}, {OsType})\n" +
            "  Manufacturer:  {Manufacturer}\n" +
            "  Model:         {Model}\n" +
            "  Serial Number: {SerialNumber}\n" +
            "  CPU:           {CpuModel}\n" +
            "  CPU Cores:     {CpuCores}\n" +
            "  RAM:           {RamGb} GB\n" +
            "  Domain:        {Domain}\n" +
            "  Last User:     {LastUser}\n" +
            "  Last Boot:     {LastBoot}\n" +
            "  Uptime:        {Uptime}s",
            info.Hostname, info.OsVersion, info.OsBuild, info.OsType,
            info.Manufacturer, info.Model, info.SerialNumber,
            info.CpuModel, info.CpuCores, info.RamGb,
            info.Domain, info.LastUser, info.LastBootTime, info.UptimeSeconds);

        return info;
    }

    private void CollectComputerSystem(SystemInfo info)
    {
        using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_ComputerSystem");
        foreach (var obj in searcher.Get())
        {
            info.Manufacturer = GetString(obj, "Manufacturer");
            info.Model = GetString(obj, "Model");
            info.Domain = GetString(obj, "Domain");
            info.LastUser = GetString(obj, "UserName");

            var totalMem = obj["TotalPhysicalMemory"];
            if (totalMem != null)
            {
                info.RamGb = Math.Round(Convert.ToDouble(totalMem) / (1024.0 * 1024 * 1024), 2);
            }

            break;
        }
    }

    private void CollectOperatingSystem(SystemInfo info)
    {
        using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_OperatingSystem");
        foreach (var obj in searcher.Get())
        {
            info.OsVersion = GetString(obj, "Caption").Replace("Microsoft ", "");
            info.OsBuild = GetString(obj, "BuildNumber");

            var productType = obj["ProductType"];
            if (productType != null)
            {
                var pt = Convert.ToInt32(productType);
                info.OsType = pt == 1 ? "windows_workstation" : "windows_server";
            }

            var lastBoot = obj["LastBootUpTime"];
            if (lastBoot != null)
            {
                info.LastBootTime = ManagementDateTimeConverter.ToDateTime(lastBoot.ToString()!).ToUniversalTime();
            }

            break;
        }
    }

    private void CollectProcessor(SystemInfo info)
    {
        using var searcher = new ManagementObjectSearcher("SELECT Name, NumberOfCores FROM Win32_Processor");
        foreach (var obj in searcher.Get())
        {
            info.CpuModel = GetString(obj, "Name").Trim();
            var cores = obj["NumberOfCores"];
            if (cores != null)
            {
                info.CpuCores = Convert.ToInt32(cores);
            }

            break;
        }
    }

    private void CollectBios(SystemInfo info)
    {
        using var searcher = new ManagementObjectSearcher("SELECT SerialNumber FROM Win32_BIOS");
        foreach (var obj in searcher.Get())
        {
            info.SerialNumber = GetString(obj, "SerialNumber");
            break;
        }
    }

    /// <summary>
    /// Reads CPU usage via PerformanceCounter. First read is always 0,
    /// so we wait 500ms before taking the real reading.
    /// </summary>
    public float? CollectCpuUsage()
    {
        try
        {
            using var counter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
            counter.NextValue(); // First read is always 0 — discard it
            Thread.Sleep(500);   // Wait for counter to accumulate a sample
            return (float)Math.Round(counter.NextValue(), 1);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect CPU usage");
            return null;
        }
    }

    /// <summary>
    /// Calculates RAM usage percentage from WMI Win32_OperatingSystem.
    /// </summary>
    public float? CollectRamUsage()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem");
            foreach (var obj in searcher.Get())
            {
                var total = Convert.ToDouble(obj["TotalVisibleMemorySize"]);
                var free = Convert.ToDouble(obj["FreePhysicalMemory"]);
                if (total > 0)
                    return (float)Math.Round((total - free) / total * 100, 1);
                break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect RAM usage");
        }
        return null;
    }

    /// <summary>
    /// Checks four registry locations for pending reboot indicators.
    /// </summary>
    public bool? CollectPendingReboot()
    {
        try
        {
            // a) Component Based Servicing\RebootPending key exists
            using (var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"))
            {
                if (key != null) return true;
            }

            // b) WindowsUpdate\Auto Update\RebootRequired key exists
            using (var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"))
            {
                if (key != null) return true;
            }

            // c) Session Manager — PendingFileRenameOperations value exists and is not empty
            using (var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Session Manager"))
            {
                if (key != null)
                {
                    var val = key.GetValue("PendingFileRenameOperations");
                    if (val is string[] arr && arr.Length > 0)
                        return true;
                }
            }

            // d) Microsoft\Updates — UpdateExeVolatile value is not 0
            using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Updates"))
            {
                if (key != null)
                {
                    var val = key.GetValue("UpdateExeVolatile");
                    if (val is int intVal && intVal != 0)
                        return true;
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check pending reboot status");
            return null;
        }
    }

    /// <summary>
    /// Queries Windows Defender status via WMI MSFT_MpComputerStatus.
    /// Returns nulls gracefully if Defender is not installed (e.g. Server OS).
    /// </summary>
    public (bool? Enabled, string? DefinitionsDate, int? LastScanDays) CollectDefenderStatus()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Defender",
                "SELECT AMRunningMode, AntivirusSignatureLastUpdated, QuickScanAge FROM MSFT_MpComputerStatus");
            foreach (var obj in searcher.Get())
            {
                // AMRunningMode: "Normal" means real-time protection is on
                var mode = obj["AMRunningMode"]?.ToString();
                var enabled = !string.IsNullOrEmpty(mode) &&
                              mode.Equals("Normal", StringComparison.OrdinalIgnoreCase);

                string? defsDate = null;
                var sigDate = obj["AntivirusSignatureLastUpdated"];
                if (sigDate != null)
                {
                    var dt = ManagementDateTimeConverter.ToDateTime(sigDate.ToString()!);
                    defsDate = dt.ToString("yyyy-MM-dd");
                }

                int? scanDays = null;
                var scanAge = obj["QuickScanAge"];
                if (scanAge != null)
                    scanDays = Convert.ToInt32(scanAge);

                return (enabled, defsDate, scanDays);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect Windows Defender status (may not be installed)");
        }
        return (null, null, null);
    }

    /// <summary>
    /// Queries BitLocker encryption status via WMI Win32_EncryptableVolume.
    /// Returns empty list if WMI namespace doesn't exist (Home editions).
    /// </summary>
    public List<BitLockerDrive> CollectBitLockerStatus()
    {
        var drives = new List<BitLockerDrive>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\cimv2\Security\MicrosoftVolumeEncryption",
                "SELECT DriveLetter, ProtectionStatus FROM Win32_EncryptableVolume");
            foreach (var obj in searcher.Get())
            {
                var letter = obj["DriveLetter"]?.ToString();
                if (string.IsNullOrEmpty(letter)) continue;

                var status = Convert.ToInt32(obj["ProtectionStatus"] ?? 0);
                drives.Add(new BitLockerDrive
                {
                    Drive = letter,
                    Protected = status == 1
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect BitLocker status (may not be available)");
        }
        return drives;
    }

    /// <summary>
    /// Enumerates members of the local Administrators group via WinNT ADSI provider.
    /// Excludes the built-in Administrator account and the Administrators group itself.
    /// </summary>
    public List<string> CollectLocalAdmins()
    {
        var admins = new List<string>();
        try
        {
            var hostname = Environment.MachineName;
            using var group = new DirectoryEntry($"WinNT://{hostname}/Administrators,group");
            var members = (System.Collections.IEnumerable?)group.Invoke("Members");
            if (members == null) return admins;
            foreach (var member in members)
            {
                using var entry = new DirectoryEntry(member);
                var path = entry.Path; // WinNT://DOMAIN/username or WinNT://HOSTNAME/username
                var name = entry.Name;

                // Extract domain from path: WinNT://DOMAIN/username → DOMAIN
                var domain = string.Empty;
                if (path.StartsWith("WinNT://", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = path.Substring(8).Split('/');
                    if (parts.Length >= 2)
                        domain = parts[0];
                }

                // Skip well-known built-in accounts
                if (name.Equals("Administrator", StringComparison.OrdinalIgnoreCase) &&
                    domain.Equals(hostname, StringComparison.OrdinalIgnoreCase))
                    continue;

                var formatted = string.IsNullOrEmpty(domain) ? name : $"{domain}\\{name}";
                admins.Add(formatted);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate local administrators");
        }
        return admins;
    }

    private static string GetString(ManagementBaseObject obj, string property)
    {
        try
        {
            return obj[property]?.ToString() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
