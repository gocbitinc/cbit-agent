using System.Text.Json.Serialization;

namespace CbitAgent.Models;

public class DiskInfo
{
    [JsonPropertyName("drive_letter")]
    public string DriveLetter { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("total_gb")]
    public double TotalGb { get; set; }

    [JsonPropertyName("used_gb")]
    public double UsedGb { get; set; }

    [JsonPropertyName("free_gb")]
    public double FreeGb { get; set; }

    [JsonPropertyName("file_system")]
    public string FileSystem { get; set; } = string.Empty;

    /// <summary>
    /// SMART health status derived from predict_failure for the physical disk backing this volume.
    /// Values: "critical" (predict_failure=true), "healthy" (predict_failure=false), "unknown" (SMART unavailable).
    /// Set before every check-in payload is sent.
    /// </summary>
    [JsonPropertyName("smart_status")]
    public string SmartStatus { get; set; } = "unknown";
}
