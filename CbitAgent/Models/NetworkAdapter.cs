using System.Text.Json.Serialization;

namespace CbitAgent.Models;

public class NetworkAdapter
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("mac_address")]
    public string MacAddress { get; set; } = string.Empty;

    [JsonPropertyName("ip_address")]
    public string? IpAddress { get; set; }

    [JsonPropertyName("subnet_mask")]
    public string? SubnetMask { get; set; }

    [JsonPropertyName("default_gateway")]
    public string? DefaultGateway { get; set; }

    [JsonPropertyName("dns_servers")]
    public List<string> DnsServers { get; set; } = new();

    [JsonPropertyName("dhcp_enabled")]
    public bool DhcpEnabled { get; set; }

    [JsonPropertyName("is_primary")]
    public bool IsPrimary { get; set; }

    [JsonPropertyName("wifi_ssid")]
    public string? WifiSsid { get; set; }

    [JsonPropertyName("wifi_signal_strength")]
    public int? WifiSignalStrength { get; set; }

    [JsonPropertyName("wifi_link_speed")]
    public string? WifiLinkSpeed { get; set; }

    [JsonPropertyName("wifi_frequency_band")]
    public string? WifiFrequencyBand { get; set; }
}
