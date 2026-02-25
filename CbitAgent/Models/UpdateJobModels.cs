using System.Text.Json.Serialization;

namespace CbitAgent.Models;

public class UpdateJobResult
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "success";

    [JsonPropertyName("kbs_installed")]
    public List<string> KbsInstalled { get; set; } = new();

    [JsonPropertyName("kbs_failed")]
    public List<string> KbsFailed { get; set; } = new();

    [JsonPropertyName("reboot_required")]
    public bool RebootRequired { get; set; }

    [JsonPropertyName("error_message")]
    public string? ErrorMessage { get; set; }
}

public class UpdateProgress
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("current_kb")]
    public string? CurrentKb { get; set; }

    [JsonPropertyName("progress_percent")]
    public int ProgressPercent { get; set; }
}
