using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Csmfd.Server.Hosting;

/// <summary>A private IPv4 address the user may choose for the listener.</summary>
internal sealed record LocalAddress(IPAddress Address, string InterfaceName, int InterfaceIndex, bool LooksLikeVpn);

/// <summary>
/// Candidate listener addresses (spec 41.4): up, non-loopback interfaces with a private IPv4
/// (10/8, 172.16/12, 192.168/16). VPN-like adapters are listed but flagged; nothing is picked automatically.
/// </summary>
internal static class NetworkAddresses
{
    private static readonly string[] VpnMarkers = ["vpn", "tap", "tun", "wireguard", "tailscale", "zerotier", "hamachi", "openvpn", "nord", "proton"];

    public static IReadOnlyList<LocalAddress> GetPrivateIPv4()
    {
        var result = new List<LocalAddress>();
        foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (network.OperationalStatus != OperationalStatus.Up
                || network.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            // GetIPv4Properties throws (10043) for adapters without IPv4 instead of returning null.
            if (!network.Supports(NetworkInterfaceComponent.IPv4))
            {
                continue;
            }

            IPInterfaceProperties properties;
            int index;
            try
            {
                properties = network.GetIPProperties();
                index = properties.GetIPv4Properties()?.Index ?? -1;
            }
            catch (NetworkInformationException)
            {
                continue;
            }

            var looksLikeVpn = network.NetworkInterfaceType == NetworkInterfaceType.Ppp
                               || VpnMarkers.Any(marker =>
                                   network.Description.Contains(marker, StringComparison.OrdinalIgnoreCase)
                                   || network.Name.Contains(marker, StringComparison.OrdinalIgnoreCase));

            foreach (var unicast in properties.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily == AddressFamily.InterNetwork && IsPrivate(unicast.Address))
                {
                    result.Add(new LocalAddress(unicast.Address, network.Name, index, looksLikeVpn));
                }
            }
        }

        return result;
    }

    public static bool IsPrivate(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        return bytes[0] == 10
               || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
               || (bytes[0] == 192 && bytes[1] == 168);
    }

    /// <summary>The configured address if it is private and currently assigned to an up interface.</summary>
    public static LocalAddress? Find(string? configured)
    {
        if (configured is null || !IPAddress.TryParse(configured, out var address) || !IsPrivate(address))
        {
            return null;
        }

        return GetPrivateIPv4().FirstOrDefault(candidate => candidate.Address.Equals(address));
    }
}
