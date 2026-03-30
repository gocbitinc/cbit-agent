using System.Text.Json.Serialization;

namespace CbitAgent.Configuration;

public class AgentConfig
{
    [JsonPropertyName("server_url")]
    public string ServerUrl { get; set; } = "https://axis.gocbit.com";

    [JsonPropertyName("customer_key")]
    public string CustomerKey { get; set; } = string.Empty;

    [JsonPropertyName("agent_id")]
    public string? AgentId { get; set; }

    [JsonPropertyName("agent_token")]
    public string? AgentToken { get; set; }

    [JsonPropertyName("check_in_interval_minutes")]
    public int CheckInIntervalMinutes { get; set; } = 5;

    [JsonPropertyName("script_signing_secret")]
    public string? ScriptSigningSecret { get; set; }

    [JsonPropertyName("screenconnect_instance_id")]
    public string? ScreenConnectInstanceId { get; set; }

    [JsonIgnore]
    public bool IsRegistered => !string.IsNullOrEmpty(AgentId) && !string.IsNullOrEmpty(AgentToken);
}
