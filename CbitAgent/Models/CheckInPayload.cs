using System.Text.Json.Serialization;

namespace CbitAgent.Models;

public class CheckInPayload
{
    [JsonPropertyName("agent_id")]
    public string AgentId { get; set; } = string.Empty;

    [JsonPropertyName("agent_version")]
    public string AgentVersion { get; set; } = string.Empty;

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("system_info")]
    public SystemInfo SystemInfo { get; set; } = new();

    [JsonPropertyName("network_adapters")]
    public List<NetworkAdapter> NetworkAdapters { get; set; } = new();

    [JsonPropertyName("disks")]
    public List<DiskInfo> Disks { get; set; } = new();

    [JsonPropertyName("smart_data")]
    public List<SmartData> SmartData { get; set; } = new();

    [JsonPropertyName("screenconnect_guid")]
    public string? ScreenConnectGuid { get; set; }

    [JsonPropertyName("wan_ip")]
    public string? WanIp { get; set; }

    [JsonPropertyName("cpu_usage")]
    public float? CpuUsage { get; set; }

    [JsonPropertyName("ram_usage")]
    public float? RamUsage { get; set; }

    [JsonPropertyName("pending_reboot")]
    public bool? PendingReboot { get; set; }

    [JsonPropertyName("defender_enabled")]
    public bool? DefenderEnabled { get; set; }

    [JsonPropertyName("defender_definitions_date")]
    public string? DefenderDefinitionsDate { get; set; }

    [JsonPropertyName("defender_last_scan_days")]
    public int? DefenderLastScanDays { get; set; }

    [JsonPropertyName("bitlocker_status")]
    public List<BitLockerDrive>? BitLockerStatus { get; set; }

    [JsonPropertyName("local_admins")]
    public List<string>? LocalAdmins { get; set; }
}

public class BitLockerDrive
{
    [JsonPropertyName("drive")]
    public string Drive { get; set; } = string.Empty;

    [JsonPropertyName("protected")]
    public bool Protected { get; set; }
}
