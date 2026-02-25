using System.Text.Json.Serialization;

namespace CbitAgent.Models;

public class SmartData
{
    [JsonPropertyName("disk_identifier")]
    public string DiskIdentifier { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = "unknown";

    [JsonPropertyName("attributes")]
    public Dictionary<string, object> Attributes { get; set; } = new();
}
