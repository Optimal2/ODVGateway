using ODVGateway.Options;

namespace ODVGateway.Services;

internal static class WebClientSourceFallbackPolicy
{
    public static bool ShouldProxy(ODVGatewayOptions options, int sourceCount)
    {
        var fallback = options.WebClientSourceFallback;
        if (!fallback.Enabled || !fallback.ProxyThroughGateway) return false;

        var threshold = Math.Max(0, fallback.ProxyThroughGatewayAboveSourceCount);
        return threshold <= 0 || sourceCount >= threshold;
    }
}
