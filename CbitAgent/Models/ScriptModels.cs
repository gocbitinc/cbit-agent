using System.Text.Json.Serialization;

namespace CbitAgent.Models;

public class PendingScript
{
    [JsonPropertyName("execution_id")]
    public int ExecutionId { get; set; }

    [JsonPropertyName("script_content")]
    public string ScriptContent { get; set; } = string.Empty;

    [JsonPropertyName("variables")]
    public Dictionary<string, string>? Variables { get; set; }

    [JsonPropertyName("timeout_seconds")]
    public int TimeoutSeconds { get; set; } = 600;

    [JsonPropertyName("files")]
    public List<ScriptFile>? Files { get; set; }
}

public class ScriptFile
{
    [JsonPropertyName("filename")]
    public string Filename { get; set; } = string.Empty;

    [JsonPropertyName("download_url")]
    public string DownloadUrl { get; set; } = string.Empty;
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
