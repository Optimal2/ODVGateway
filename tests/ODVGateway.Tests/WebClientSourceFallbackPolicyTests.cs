using ODVGateway.Options;
using ODVGateway.Services;

namespace ODVGateway.Tests;

public sealed class WebClientSourceFallbackPolicyTests
{
    [Theory]
    [InlineData(false, true, 0, 0, false)]
    [InlineData(true, false, 0, 1000, false)]
    [InlineData(true, true, 0, 1, true)]
    [InlineData(true, true, 1000, 999, false)]
    [InlineData(true, true, 1000, 1000, true)]
    [InlineData(true, true, -1, 1, true)]
    public void ShouldProxy_UsesOneThresholdContractForBundleAndSourceEndpoint(
        bool enabled,
        bool proxyThroughGateway,
        int threshold,
        int sourceCount,
        bool expected)
    {
        var options = new ODVGatewayOptions
        {
            WebClientSourceFallback = new WebClientSourceFallbackOptions
            {
                Enabled = enabled,
                ProxyThroughGateway = proxyThroughGateway,
                ProxyThroughGatewayAboveSourceCount = threshold
            }
        };

        Assert.Equal(expected, WebClientSourceFallbackPolicy.ShouldProxy(options, sourceCount));
    }
}
