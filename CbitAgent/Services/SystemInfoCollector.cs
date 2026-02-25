using System.Management;
using CbitAgent.Models;
using Microsoft.Extensions.Logging;

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

        _logger.LogInformation(
            "System info collected:\n" +
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
