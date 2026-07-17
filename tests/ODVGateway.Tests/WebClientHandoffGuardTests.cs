using Microsoft.AspNetCore.Http;
using ODVGateway.Options;
using ODVGateway.Services;

namespace ODVGateway.Tests;

public sealed class WebClientHandoffGuardTests
{
    private const string AllowedInitiator = "https://webclient.example";

    private static WebClientHandoffGuard CreateGuard(
        string[]? allowedInitiatorUrls = null,
        bool allowMissingInitiatorHeaders = false)
    {
        var options = new ODVGatewayOptions
        {
            WebClientHandoff = new WebClientHandoffOptions
            {
                AllowedInitiatorUrls = allowedInitiatorUrls ?? [AllowedInitiator],
                AllowMissingInitiatorHeaders = allowMissingInitiatorHeaders
            }
        };

        return new WebClientHandoffGuard(new StaticOptionsMonitor<ODVGatewayOptions>(options));
    }

    private static HttpRequest CreateRequest()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("gateway.example");
        return context.Request;
    }

    [Fact]
    public void Validate_EmptyAllowedInitiatorUrls_AllowsRequest()
    {
        var guard = CreateGuard(allowedInitiatorUrls: []);
        var request = CreateRequest();

        var result = guard.Validate(request);

        Assert.True(result.IsAllowed);
        Assert.Equal(0, guard.RejectedCount);
    }

    [Fact]
    public void Validate_MatchingReferer_AllowsRequest()
    {
        var guard = CreateGuard();
        var request = CreateRequest();
        request.Headers.Referer = $"{AllowedInitiator}/app/documents";

        var result = guard.Validate(request);

        Assert.True(result.IsAllowed);
        Assert.NotNull(result.Initiator);
    }

    [Fact]
    public void Validate_MatchingOrigin_AllowsRequest()
    {
        var guard = CreateGuard();
        var request = CreateRequest();
        request.Headers.Origin = AllowedInitiator;

        var result = guard.Validate(request);

        Assert.True(result.IsAllowed);
        Assert.NotNull(result.Initiator);
    }

    [Fact]
    public void Validate_NonMatchingInitiator_RejectsAndIncrementsRejectedCount()
    {
        var guard = CreateGuard();
        var request = CreateRequest();
        request.Headers.Referer = "https://evil.example/phishing";

        var result = guard.Validate(request);

        Assert.False(result.IsAllowed);
        Assert.NotNull(result.Reason);
        Assert.Equal(1, guard.RejectedCount);

        guard.Validate(request);
        Assert.Equal(2, guard.RejectedCount);
    }

    [Fact]
    public void Validate_MissingInitiatorHeaders_AllowsWhenConfigured()
    {
        var guard = CreateGuard(allowMissingInitiatorHeaders: true);
        var request = CreateRequest();

        var result = guard.Validate(request);

        Assert.True(result.IsAllowed);
        Assert.Equal(0, guard.RejectedCount);
    }

    [Fact]
    public void Validate_MissingInitiatorHeaders_RejectsWhenNotConfigured()
    {
        var guard = CreateGuard(allowMissingInitiatorHeaders: false);
        var request = CreateRequest();

        var result = guard.Validate(request);

        Assert.False(result.IsAllowed);
        Assert.NotNull(result.Reason);
        Assert.Equal(1, guard.RejectedCount);
    }
}
