using System.Net;
using System.Net.Sockets;

namespace Compressarr.Core.Auth;

/// <summary>What counts as "the local network", as Sonarr and Radarr decide it: the address of the TCP peer (never a header) is loopback, a private IPv4 range
/// (10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16), IPv4 link-local (169.254.0.0/16), or IPv6 link-local, unique-local or site-local. An IPv4 address wrapped in IPv6
/// (<c>::ffff:192.168.1.5</c>) is unwrapped first. Carrier-grade NAT space (100.64.0.0/10, which Tailscale uses) is a separate question, answered only on request.</summary>
public static class LocalNetwork
{
    public static bool IsLoopback(IPAddress address) => IPAddress.IsLoopback(Unwrap(address));

    public static bool IsLocalAddress(IPAddress address)
    {
        var ip = Unwrap(address);
        if (IPAddress.IsLoopback(ip)) return true;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return (b[0] == 169 && b[1] == 254)                   // link-local
                || b[0] == 10                                     // 10.0.0.0/8
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)      // 172.16.0.0/12
                || (b[0] == 192 && b[1] == 168);                  // 192.168.0.0/16
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6) return ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal || ip.IsIPv6SiteLocal;
        return false;
    }

    /// <summary>100.64.0.0/10: shared address space used by carrier-grade NAT and by Tailscale.</summary>
    public static bool IsCgnatAddress(IPAddress address)
    {
        var ip = Unwrap(address);
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return b[0] == 100 && b[1] >= 64 && b[1] <= 127;
    }

    private static IPAddress Unwrap(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
