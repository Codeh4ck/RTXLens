using System.Net;
using System.Net.Sockets;
using System.Text.Json.Serialization;
using RTXLens.Contracts.Monitoring;

namespace RTXLens.Contracts.Mobile;

public sealed record MobileAccessSettings(
    bool Enabled = false,
    int Port = MobileAccessProtocol.DefaultPort,
    NoIpSettings? NoIp = null,
    bool OpenFirewall = false,
    MobilePortForwarding? PortForwarding = null,
    string? LocalAddress = null,
    string? PublicAddress = null,
    int WarningHistoryDays = MobileAccessProtocol.DefaultWarningHistoryDays)
{
    [JsonIgnore]
    public bool IsValid =>
        Port is >= 1024 and <= 65535 and not (ServiceEndpoint.LivePort or ServiceEndpoint.DebugPort)
        && WarningHistoryDays is >= 1 and <= 365
        && (NoIp is null || NoIp.IsValid)
        && (PortForwarding is null || PortForwarding.IsValid)
        && (LocalAddress is null || TryGetLocalIpv4(LocalAddress, out _))
        && (PublicAddress is null || (IPAddress.TryParse(PublicAddress, out var address) && IsPublicIpv4(address)));

    public static bool TryGetLocalIpv4(string? text, out IPAddress address)
    {
        if (IPAddress.TryParse(text, out var parsed) && parsed.AddressFamily == AddressFamily.InterNetwork
            && !IPAddress.IsLoopback(parsed) && !parsed.Equals(IPAddress.Any))
        {
            address = parsed;
            return true;
        }

        address = IPAddress.None;
        return false;
    }

    public static bool IsPublicIpv4(IPAddress? address)
    {
        if (address?.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var octets = address.GetAddressBytes();
        var first = octets[0];
        var second = octets[1];

        return first is not (0 or 10 or 127 or >= 224)
            && (first != 100 || second is < 64 or > 127)
            && (first != 169 || second != 254)
            && (first != 172 || second is < 16 or > 31)
            && (first != 192 || second != 168)
            && (first != 198 || second is not (18 or 19));
    }
}

public sealed record NoIpSettings(string Hostname, string KeyUsername, string KeyPassword)
{
    [JsonIgnore]
    public bool IsValid =>
        Hostname is { Length: > 0 and <= 253 } && Uri.CheckHostName(Hostname) == UriHostNameType.Dns
        && KeyUsername is { Length: > 0 and <= 128 } && !KeyUsername.Contains(':')
        && KeyPassword is { Length: > 0 and <= 256 };
}

public sealed record MobilePortForwarding(string LocalAddress, int ExternalPort)
{
    [JsonIgnore]
    public bool IsValid => ExternalPort is >= 1024 and <= 65535 && MobileAccessSettings.TryGetLocalIpv4(LocalAddress, out _);
}
