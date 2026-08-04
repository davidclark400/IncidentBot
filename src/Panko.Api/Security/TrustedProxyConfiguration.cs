using System.Net;
using Panko.Api.Options;
using Microsoft.AspNetCore.HttpOverrides;

namespace Panko.Api.Security;

public static class TrustedProxyConfiguration
{
    public static bool IsValid(TrustedProxyOptions options) =>
        options.KnownProxies.All(IsValidProxy)
        && options.KnownNetworks.All(IsValidNetwork);

    public static ForwardedHeadersOptions Create(TrustedProxyOptions configured)
    {
        ArgumentNullException.ThrowIfNull(configured);
        if (!IsValid(configured))
        {
            throw new InvalidOperationException("Trusted proxy addresses contain an invalid or catch-all IP address or network.");
        }

        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            ForwardLimit = configured.ForwardLimit,
            RequireHeaderSymmetry = true
        };
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        foreach (var proxy in configured.KnownProxies)
        {
            options.KnownProxies.Add(IPAddress.Parse(proxy));
        }
        foreach (var network in configured.KnownNetworks)
        {
            options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
        }
        return options;
    }

    private static bool IsValidProxy(string value) =>
        IPAddress.TryParse(value, out var address)
        && !address.Equals(IPAddress.Any)
        && !address.Equals(IPAddress.IPv6Any);

    private static bool IsValidNetwork(string value) =>
        System.Net.IPNetwork.TryParse(value, out var network)
        && network.PrefixLength > 0;
}
