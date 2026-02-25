using System.Text.Json.Serialization;

namespace CbitAgent.Models;

public class SystemInfo
{
    [JsonPropertyName("hostname")]
    public string Hostname { get; set; } = string.Empty;

    [JsonPropertyName("os_type")]
    public string OsType { get; set; } = string.Empty;

    [JsonPropertyName("os_version")]
    public string OsVersion { get; set; } = string.Empty;

    [JsonPropertyName("os_build")]
    public string OsBuild { get; set; } = string.Empty;

    [JsonPropertyName("manufacturer")]
    public string Manufacturer { get; set; } = string.Empty;

    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("serial_number")]
    public string SerialNumber { get; set; } = string.Empty;

    [JsonPropertyName("cpu_model")]
    public string CpuModel { get; set; } = string.Empty;

    [JsonPropertyName("cpu_cores")]
    public int CpuCores { get; set; }

    [JsonPropertyName("ram_gb")]
    public double RamGb { get; set; }

    [JsonPropertyName("domain")]
    public string Domain { get; set; } = string.Empty;

    [JsonPropertyName("last_user")]
    public string LastUser { get; set; } = string.Empty;

    [JsonPropertyName("last_boot_time")]
    public DateTime? LastBootTime { get; set; }

    [JsonPropertyName("uptime_seconds")]
    public long UptimeSeconds { get; set; }
}
