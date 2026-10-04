using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace FlowDeck.Infrastructure;

public sealed record M5LanAddress(string InterfaceId, string Address, string Mask, string Name)
{
    public string Label => $"{Name} — {Address}";
    public bool Contains(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var remote = address.GetAddressBytes(); var local = IPAddress.Parse(Address).GetAddressBytes(); var mask = IPAddress.Parse(Mask).GetAddressBytes();
        return remote.Zip(local, (a, b) => (a, b)).Zip(mask, (pair, m) => (pair.a & m) == (pair.b & m)).All(equal => equal);
    }
}
public static class M5Network
{
    public const int DiscoveryPort = 47632;
    public static bool PrivateAddress(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = address.GetAddressBytes();
        return b[0] == 10 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168;
    }
    public static M5LanAddress[] Addresses() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses.Where(a => PrivateAddress(a.Address))
            .Select(a => new M5LanAddress(n.Id, a.Address.ToString(), a.IPv4Mask.ToString(), n.Name))).ToArray();
}
