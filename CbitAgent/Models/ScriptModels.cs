using System.Text.Json.Serialization;

namespace CbitAgent.Models;

public class PendingScript
{
    [JsonPropertyName("execution_id")]
    public string ExecutionId { get; set; } = string.Empty;

    [JsonPropertyName("script_content")]
    public string ScriptContent { get; set; } = string.Empty;

    [JsonPropertyName("variables")]
    public Dictionary<string, string>? Variables { get; set; }

    [JsonPropertyName("timeout_seconds")]
    public int TimeoutSeconds { get; set; } = 600;

    [JsonPropertyName("files")]
    public List<ScriptFile>? Files { get; set; }

    [JsonPropertyName("script_signature")]
    public string? ScriptSignature { get; set; }
}

public class ScriptFile
{
    [JsonPropertyName("filename")]
    public string Filename { get; set; } = string.Empty;

    [JsonPropertyName("download_url")]
    public string DownloadUrl { get; set; } = string.Empty;

    /// <summary>
    /// SHA-256 hex digest of the file content, included in the RSA-PSS signed payload.
    /// Agent verifies downloaded bytes against this hash before writing to disk.
    /// Server MUST include this field for every helper file — scripts without it are rejected.
    /// TODO (server): include file_hash (SHA-256 hex) per helper file in signed payload — see MEMORY.md
    /// </summary>
    [JsonPropertyName("file_hash")]
    public string? FileHash { get; set; }
}

public class PowerShellResult
{
    public int ExitCode { get; set; }
    public string Stdout { get; set; } = string.Empty;
    public string Stderr { get; set; } = string.Empty;
    public bool TimedOut { get; set; }
}

public class ScriptResult
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("exit_code")]
    public int ExitCode { get; set; }

    [JsonPropertyName("stdout")]
    public string Stdout { get; set; } = string.Empty;

    [JsonPropertyName("stderr")]
    public string Stderr { get; set; } = string.Empty;

    [JsonPropertyName("started_at")]
    public DateTime StartedAt { get; set; }

    [JsonPropertyName("completed_at")]
    public DateTime CompletedAt { get; set; }
}
