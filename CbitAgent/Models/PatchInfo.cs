using System.Text.Json.Serialization;

namespace CbitAgent.Models;

public class InstalledPatch
{
    [JsonPropertyName("kb_number")]
    public string KbNumber { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("installed_on")]
    public DateTime? InstalledOn { get; set; }

    [JsonPropertyName("category")]
    public string? Category { get; set; }
}

public class PendingPatch
{
    [JsonPropertyName("kb_number")]
    public string KbNumber { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("severity")]
    public string? Severity { get; set; }

    [JsonPropertyName("category")]
    public string? Category { get; set; }
}
