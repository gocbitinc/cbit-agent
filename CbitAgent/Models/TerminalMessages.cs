using System.Text.Json;
using System.Text.Json.Serialization;

namespace CbitAgent.Models;

// ── Inbound: server → agent ──

public class WsMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("session_id")]
    public string? SessionId { get; set; }

    [JsonPropertyName("shell_type")]
    public string? ShellType { get; set; }

    [JsonPropertyName("data")]
    public string? Data { get; set; }

    [JsonPropertyName("cols")]
    public int? Cols { get; set; }

    [JsonPropertyName("rows")]
    public int? Rows { get; set; }

    // ── Windows Update fields ──

    [JsonPropertyName("job_id")]
    public string? JobId { get; set; }

    [JsonPropertyName("job_asset_id")]
    public string? JobAssetId { get; set; }

    [JsonPropertyName("kb_number")]
    public string? KbNumber { get; set; }

    [JsonPropertyName("kb_numbers")]
    public List<string>? KbNumbers { get; set; }

    [JsonPropertyName("reboot_behavior")]
    public string? RebootBehavior { get; set; }

    [JsonPropertyName("policy_id")]
    public string? PolicyId { get; set; }

    [JsonPropertyName("policy")]
    public JsonElement? Policy { get; set; }
}

// ── Outbound: agent → server ──

public class WsOutMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("session_id")]
    public string? SessionId { get; set; }

    [JsonPropertyName("data")]
    public string? Data { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    // ── Windows Update fields ──

    [JsonPropertyName("job_id")]
    public string? JobId { get; set; }

    [JsonPropertyName("job_asset_id")]
    public string? JobAssetId { get; set; }

    [JsonPropertyName("kb_number")]
    public string? KbNumber { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("pending_patches")]
    public List<PendingPatch>? PendingPatches { get; set; }

    [JsonPropertyName("kbs_installed")]
    public List<string>? KbsInstalled { get; set; }

    [JsonPropertyName("kbs_failed")]
    public List<string>? KbsFailed { get; set; }

    [JsonPropertyName("reboot_required")]
    public bool? RebootRequired { get; set; }

    [JsonPropertyName("current_kb")]
    public string? CurrentKb { get; set; }

    [JsonPropertyName("progress_percent")]
    public int? ProgressPercent { get; set; }
}
