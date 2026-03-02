using System.Text.Json.Serialization;

namespace CbitAgent.Models;

public class CheckInResponse
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("commands")]
    public List<AgentCommand> Commands { get; set; } = new();

    [JsonPropertyName("check_in_interval_minutes")]
    public int CheckInIntervalMinutes { get; set; } = 5;

    [JsonPropertyName("pending_script")]
    public PendingScript? PendingScript { get; set; }
}

public class AgentCommand
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("download_url")]
    public string? DownloadUrl { get; set; }

    [JsonPropertyName("file_hash")]
    public string? FileHash { get; set; }

    [JsonPropertyName("kb_number")]
    public string? KbNumber { get; set; }

    [JsonPropertyName("job_id")]
    public string? JobId { get; set; }

    [JsonPropertyName("policy_id")]
    public string? PolicyId { get; set; }

    [JsonPropertyName("policy")]
    public Dictionary<string, object>? Policy { get; set; }
}

public class RegisterResponse
{
    [JsonPropertyName("agent_id")]
    public string AgentId { get; set; } = string.Empty;

    [JsonPropertyName("agent_token")]
    public string AgentToken { get; set; } = string.Empty;

    [JsonPropertyName("check_in_interval_minutes")]
    public int CheckInIntervalMinutes { get; set; } = 5;
}
