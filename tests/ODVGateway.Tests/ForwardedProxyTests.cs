using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ODVGateway.Models;
using ODVGateway.Options;
using ODVGateway.Services;

namespace ODVGateway.Tests;

public sealed class ForwardedProxyTests
{
    [Theory]
    [InlineData(false, "127.0.0.1", "http://localhost/bundle/")]
    [InlineData(true, "10.20.30.40", "http://localhost/bundle/")]
    [InlineData(true, "127.0.0.1", "https://public.example/bundle/")]
    public async Task ViewerBundleUrl_UsesForwardedHostOnlyFromConfiguredProxy(
        bool enabled,
        string knownProxy,
        string expectedBundlePrefix)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("localhost");
        context.Request.Headers["X-Forwarded-Proto"] = "https";
        context.Request.Headers["X-Forwarded-Host"] = "public.example";
        context.Request.Headers["X-Forwarded-For"] = "203.0.113.10";

        if (enabled)
        {
            var options = ForwardedProxyConfiguration.CreateOptions(new ForwardedProxyOptions
            {
                Enabled = true,
                KnownProxies = [knownProxy]
            });
            var middleware = new ForwardedHeadersMiddleware(
                _ => Task.CompletedTask,
                NullLoggerFactory.Instance,
                Microsoft.Extensions.Options.Options.Create(options));
            await middleware.Invoke(context);
        }

        var method = typeof(Program).GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Single(candidate => candidate.Name.Contains("BuildViewerBundleUrl", StringComparison.Ordinal));
        var location = Assert.IsType<string>(method.Invoke(null, [context.Request, CreateSession()]));
        var queryStart = location.IndexOf('?', StringComparison.Ordinal);
        Assert.True(queryStart >= 0, "The redirect did not contain a bundleUrl query.");
        var bundleUrl = QueryHelpers.ParseQuery(location[queryStart..])["bundleUrl"].ToString();

        Assert.NotEmpty(bundleUrl);
        Assert.StartsWith(expectedBundlePrefix, bundleUrl, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "not-an-ip")]
    [InlineData("", "")]
    public void EnabledConfiguration_RequiresOnlyValidKnownProxyAddresses(params string[] knownProxies)
    {
        Assert.Throws<InvalidOperationException>(() => ForwardedProxyConfiguration.CreateOptions(
            new ForwardedProxyOptions { Enabled = true, KnownProxies = knownProxies }));
    }

    private static GatewaySession CreateSession()
    {
        return new GatewaySession
        {
            SessionKey = "session-key",
            HandoffLookupKey = "lookup-key",
            Prep = new WebClientPrepRequest(),
            CreatedUtc = DateTimeOffset.UtcNow,
            ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(5),
            SourceFiles = []
        };
    }
}
