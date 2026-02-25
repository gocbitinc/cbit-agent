using System.Text.Json.Serialization;

namespace CbitAgent.Configuration;

public class AgentConfig
{
    /// <summary>
    /// Embedded customer key placeholder — binary-replaced in the MSI by the server
    /// before download. Must be static (not const) so the linker preserves it in the PE.
    /// Exactly 64 characters: 21-char prefix + 43 zeros.
    /// </summary>
    private static string EmbeddedCustomerKey =
        "CBIT_PLACEHOLDER_KEY_0000000000000000000000000000000000000000000";

    /// <summary>
    /// Returns the embedded key if it has been replaced with a real value
    /// (i.e. no longer starts with the placeholder prefix).
    /// </summary>
    public static string? GetEmbeddedCustomerKey()
    {
        // If the server replaced the placeholder, the string will no longer
        // start with the well-known prefix.
        if (!EmbeddedCustomerKey.StartsWith("CBIT_PLACEHOLDER_KEY_"))
            return EmbeddedCustomerKey;

        return null;
    }

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

    [JsonIgnore]
    public bool IsRegistered => !string.IsNullOrEmpty(AgentId) && !string.IsNullOrEmpty(AgentToken);
}
