using System.Text.Json.Serialization;

namespace CbitAgent.Models;

public class InstalledApp
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("publisher")]
    public string? Publisher { get; set; }

    [JsonPropertyName("install_date")]
    public string? InstallDate { get; set; }
}
