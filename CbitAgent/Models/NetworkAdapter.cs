using System.Text.Json.Serialization;

namespace CbitAgent.Models;

public class NetworkAdapter
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("mac")]
    public string Mac { get; set; } = string.Empty;

    [JsonPropertyName("dhcp")]
    public bool Dhcp { get; set; }

    [JsonPropertyName("gateway")]
    public string? Gateway { get; set; }

    [JsonPropertyName("addresses")]
    public List<AdapterAddress> Addresses { get; set; } = new();

    [JsonPropertyName("dns_servers")]
    public List<string> DnsServers { get; set; } = new();

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

public class AdapterAddress
{
    [JsonPropertyName("ip")]
    public string Ip { get; set; } = string.Empty;

    [JsonPropertyName("subnet")]
    public string? Subnet { get; set; }
}
