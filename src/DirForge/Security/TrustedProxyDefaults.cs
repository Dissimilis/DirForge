using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using NetIPNetwork = System.Net.IPNetwork;

namespace DirForge.Security;

internal static class TrustedProxyDefaults
{
    private static readonly NetIPNetwork[] DefaultKnownNetworks =
    [
        NetIPNetwork.Parse("100.64.0.0/10"),
        NetIPNetwork.Parse("10.0.0.0/8"),
        NetIPNetwork.Parse("172.16.0.0/12"),
        NetIPNetwork.Parse("192.168.0.0/16"),
        NetIPNetwork.Parse("fc00::/7")
    ];

    public static void AddKnownNetworks(ForwardedHeadersOptions options)
    {
        foreach (var network in DefaultKnownNetworks)
        {
            options.KnownIPNetworks.Add(network);
        }
    }

    public static bool IsTrusted(IPAddress? remoteIpAddress, IReadOnlyCollection<IPAddress> knownProxies)
    {
        if (remoteIpAddress is null)
        {
            return false;
        }

        var normalizedAddress = Normalize(remoteIpAddress);
        if (knownProxies.Count > 0)
        {
            return knownProxies.Any(proxy => Normalize(proxy).Equals(normalizedAddress));
        }

        return IPAddress.IsLoopback(normalizedAddress) ||
               DefaultKnownNetworks.Any(network => network.Contains(normalizedAddress));
    }

    private static IPAddress Normalize(IPAddress address)
    {
        return address.IsIPv4MappedToIPv6
            ? address.MapToIPv4()
            : address;
    }
}
