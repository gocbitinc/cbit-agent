using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using CbitAgent.Models;
using Microsoft.Extensions.Logging;

namespace CbitAgent.Services;

public class NetworkInfoCollector
{
    private readonly ILogger<NetworkInfoCollector> _logger;
    private string? _cachedWanIp;
    private DateTime _wanIpCacheTime = DateTime.MinValue;
    private static readonly TimeSpan WanIpCacheLifetime = TimeSpan.FromMinutes(5);
    private static readonly HttpClient WanIpClient = new() { Timeout = TimeSpan.FromSeconds(5) };

    public NetworkInfoCollector(ILogger<NetworkInfoCollector> logger)
    {
        _logger = logger;
    }

    public List<NetworkAdapter> CollectAdapters()
    {
        var adapters = new List<NetworkAdapter>();

        try
        {
            // Collect ALL adapters — physical, virtual, VPN, Hyper-V, tunnel, loopback
            var interfaces = NetworkInterface.GetAllNetworkInterfaces();

            // Parse WiFi details once
            var wifiDetails = GetWifiDetails();

            foreach (var ni in interfaces)
            {
                try
                {
                    var adapter = MapAdapter(ni);

                    // Enrich WiFi adapters with netsh data
                    if (adapter.Type == "Wireless" && wifiDetails != null)
                    {
                        adapter.WifiSsid = wifiDetails.Ssid;
                        adapter.WifiSignalStrength = wifiDetails.SignalStrength;
                        adapter.WifiLinkSpeed = wifiDetails.LinkSpeed;
                        adapter.WifiFrequencyBand = wifiDetails.FrequencyBand;
                    }

                    adapters.Add(adapter);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to collect info for adapter {Name}", ni.Description);
                }
            }

            // Determine primary adapter (first up adapter with a default gateway)
            var primary = adapters.FirstOrDefault(a => !string.IsNullOrEmpty(a.Gateway));
            if (primary != null)
            {
                primary.IsPrimary = true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enumerate network adapters");
        }

        _logger.LogInformation("Collected {Count} network adapters", adapters.Count);
        return adapters;
    }

    private NetworkAdapter MapAdapter(NetworkInterface ni)
    {
        var ipProps = ni.GetIPProperties();

        var adapter = new NetworkAdapter
        {
            Name = ni.Description,
            Type = MapType(ni.NetworkInterfaceType),
            Mac = FormatMac(ni.GetPhysicalAddress()),
            Dhcp = false
        };

        // Collect ALL IP addresses bound to this adapter (IPv4 and IPv6)
        foreach (var unicast in ipProps.UnicastAddresses)
        {
            var addr = new AdapterAddress { Ip = unicast.Address.ToString() };

            if (unicast.Address.AddressFamily == AddressFamily.InterNetwork)
                addr.Subnet = unicast.IPv4Mask?.ToString();

            adapter.Addresses.Add(addr);
        }

        // Default gateway (prefer IPv4)
        var gateway = ipProps.GatewayAddresses
            .FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork)
            ?? ipProps.GatewayAddresses.FirstOrDefault();
        adapter.Gateway = gateway?.Address.ToString();

        // DNS servers
        adapter.DnsServers = ipProps.DnsAddresses
            .Select(d => d.ToString())
            .ToList();

        // DHCP
        try
        {
            var ipv4Props = ipProps.GetIPv4Properties();
            adapter.Dhcp = ipv4Props.IsDhcpEnabled;
        }
        catch
        {
            // Some adapters don't support IPv4 properties (e.g. IPv6-only, tunnel)
        }

        return adapter;
    }

    private static string MapType(NetworkInterfaceType type)
    {
        return type switch
        {
            NetworkInterfaceType.Ethernet => "Ethernet",
            NetworkInterfaceType.GigabitEthernet => "Ethernet",
            NetworkInterfaceType.FastEthernetT => "Ethernet",
            NetworkInterfaceType.FastEthernetFx => "Ethernet",
            NetworkInterfaceType.Wireless80211 => "Wireless",
            NetworkInterfaceType.Loopback => "Loopback",
            NetworkInterfaceType.Tunnel => "Tunnel",
            NetworkInterfaceType.Ppp => "VPN",
            _ => type.ToString()
        };
    }

    private static string FormatMac(PhysicalAddress mac)
    {
        var bytes = mac.GetAddressBytes();
        if (bytes.Length == 0) return string.Empty;
        return string.Join(":", bytes.Select(b => b.ToString("X2")));
    }

    public async Task<string?> GetWanIpAsync(CancellationToken ct = default)
    {
        if (_cachedWanIp != null && DateTime.UtcNow - _wanIpCacheTime < WanIpCacheLifetime)
        {
            return _cachedWanIp;
        }

        var endpoints = new[] { "https://api.ipify.org", "https://ifconfig.me/ip", "https://icanhazip.com" };

        foreach (var endpoint in endpoints)
        {
            try
            {
                var ip = (await WanIpClient.GetStringAsync(endpoint, ct)).Trim();
                if (!string.IsNullOrEmpty(ip) && ip.Length <= 45)
                {
                    _cachedWanIp = ip;
                    _wanIpCacheTime = DateTime.UtcNow;
                    _logger.LogDebug("WAN IP: {Ip}", ip);
                    return ip;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to get WAN IP from {Endpoint}", endpoint);
            }
        }

        _logger.LogWarning("Could not determine WAN IP from any endpoint");
        return _cachedWanIp; // return stale cache if available
    }

    private WifiInfo? GetWifiDetails()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = "wlan show interfaces",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return null;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);

            if (string.IsNullOrWhiteSpace(output)) return null;

            var info = new WifiInfo();

            // Parse SSID
            var ssidMatch = Regex.Match(output, @"^\s*SSID\s*:\s*(.+)$", RegexOptions.Multiline);
            if (ssidMatch.Success)
                info.Ssid = ssidMatch.Groups[1].Value.Trim();

            // Parse Signal
            var signalMatch = Regex.Match(output, @"Signal\s*:\s*(\d+)%", RegexOptions.Multiline);
            if (signalMatch.Success)
                info.SignalStrength = int.Parse(signalMatch.Groups[1].Value);

            // Parse Receive rate (link speed)
            var rxMatch = Regex.Match(output, @"Receive rate \(Mbps\)\s*:\s*(.+)", RegexOptions.Multiline);
            if (rxMatch.Success)
                info.LinkSpeed = rxMatch.Groups[1].Value.Trim() + " Mbps";

            // Parse Radio type
            var radioMatch = Regex.Match(output, @"Radio type\s*:\s*(.+)", RegexOptions.Multiline);

            // Parse Channel to determine frequency band
            var channelMatch = Regex.Match(output, @"Channel\s*:\s*(\d+)", RegexOptions.Multiline);
            if (channelMatch.Success)
            {
                var channel = int.Parse(channelMatch.Groups[1].Value);
                info.FrequencyBand = channel >= 36 ? "5GHz" : "2.4GHz";
                // Channels 1-14 = 2.4GHz, 36+ = 5GHz, some 6GHz channels exist at 1-233 range
                if (channel > 177)
                    info.FrequencyBand = "6GHz";
            }
            else if (radioMatch.Success)
            {
                var radio = radioMatch.Groups[1].Value.Trim().ToLower();
                if (radio.Contains("ac") || radio.Contains("802.11a"))
                    info.FrequencyBand = "5GHz";
                else if (radio.Contains("802.11b") || radio.Contains("802.11g"))
                    info.FrequencyBand = "2.4GHz";
            }

            if (string.IsNullOrEmpty(info.Ssid)) return null;

            _logger.LogDebug("WiFi info: SSID={Ssid}, Signal={Signal}%, Band={Band}",
                info.Ssid, info.SignalStrength, info.FrequencyBand);

            return info;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get WiFi details from netsh");
            return null;
        }
    }

    private class WifiInfo
    {
        public string? Ssid { get; set; }
        public int? SignalStrength { get; set; }
        public string? LinkSpeed { get; set; }
        public string? FrequencyBand { get; set; }
    }
}
