using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using ODVGateway.Options;

namespace ODVGateway.Services;

internal static class ForwardedProxyConfiguration
{
    public static ForwardedHeadersOptions CreateOptions(ForwardedProxyOptions settings)
    {
        var forwardedOptions = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor |
                ForwardedHeaders.XForwardedHost |
                ForwardedHeaders.XForwardedProto,
            ForwardLimit = 1
        };
        // Opt in to exact proxy addresses only. Clearing the defaults prevents the
        // middleware from implicitly trusting loopback or any other network.
        forwardedOptions.KnownIPNetworks.Clear();
        forwardedOptions.KnownProxies.Clear();
        foreach (var configuredProxy in settings.KnownProxies
            .Where(proxy => !string.IsNullOrWhiteSpace(proxy)))
        {
            if (!IPAddress.TryParse(configuredProxy.Trim(), out var proxyAddress))
            {
                throw new InvalidOperationException(
                    "ODVGateway forwardedProxy.knownProxies entries must be valid IP addresses.");
            }

            forwardedOptions.KnownProxies.Add(proxyAddress);
        }

        if (forwardedOptions.KnownProxies.Count == 0)
        {
            throw new InvalidOperationException(
                "ODVGateway forwardedProxy.enabled requires at least one entry in forwardedProxy.knownProxies.");
        }

        return forwardedOptions;
    }
}
