using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace ODVGateway.Tests;

// HTTP-level probes for the status codes the gateway actually returns. A monitor
// reads the status line, not the payload, so /health must answer 503 when the
// payload says "degraded", and the error pages must carry their real 4xx codes.
// The factory boots the real app on the in-memory TestServer; the only
// filesystem touch is a throwaway dist folder for the healthy probe.
public sealed class GatewayHttpStatusTests
{
    [Fact]
    public async Task Health_DistUnavailable_Returns503WithDegradedPayload()
    {
        using var factory = new GatewayFactory(distPath: null);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health");
        var payload = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains(""""{"status":"degraded"""", payload);
    }

    [Fact]
    public async Task Health_DistAvailable_Returns200WithOkPayload()
    {
        var distPath = CreateDistDirectory();
        try
        {
            using var factory = new GatewayFactory(distPath);
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/health");
            var payload = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains(""""{"status":"ok"""", payload);
        }
        finally
        {
            DeleteTempDirectory(distPath);
        }
    }

    [Fact]
    public async Task Viewer_WithoutSessionData_Returns400()
    {
        using var factory = new GatewayFactory(distPath: null);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Viewer_WithoutPreparedSession_Returns404()
    {
        using var factory = new GatewayFactory(distPath: null);
        using var client = factory.CreateClient();
        var sessionData = EncodeBase64Url("""{"userId":"u1","sessionId":"s1"}""");

        using var response = await client.GetAsync("/?sessiondata=" + sessionData);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }


    [Fact]
    public async Task Viewer_BundleHandoffRedirect_KeepsInitiatorVisibleForTheFollowUpRequest()
    {
        // Measured 2026-09-10 against a real WebClient handoff with allowedInitiatorUrls set:
        // /prep and ?sessiondata passed the initiator guard, but the 302 to ?bundleUrl=...
        // carried the blanket Referrer-Policy: no-referrer, the browser applied it to the
        // request that followed the redirect, that request arrived without Referer, and the
        // guard rejected the gateway's own redirect with 403 (Operation=viewer-bundle-query).
        // The redirect must carry a policy that keeps the same-origin initiator visible.
        var distPath = CreateDistDirectory();
        try
        {
            const string initiator = "http://localhost/WebClientODV/DocumentView";
            using var factory = new GatewayFactory(distPath, allowedInitiatorUrl: "/WebClientODV/DocumentView");
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            using var prepRequest = new HttpRequestMessage(HttpMethod.Post, "/prep")
            {
                Content = new StringContent(
                    """{"userId":"u1","sessionId":"s1","aspxAuth":"a1","portableDocuments":[{"documentId":"d1","fileData":[]}]}""",
                    Encoding.UTF8,
                    "application/json")
            };
            prepRequest.Headers.Referrer = new Uri(initiator);
            using var prepResponse = await client.SendAsync(prepRequest);
            Assert.True(prepResponse.IsSuccessStatusCode, $"/prep answered {(int)prepResponse.StatusCode}");

            var sessionData = EncodeBase64Url("""{"userId":"u1","sessionId":"s1","aspxAuth":"a1","caseIds":["d1"]}""");
            using var viewerRequest = new HttpRequestMessage(HttpMethod.Get, "/?sessiondata=" + sessionData);
            viewerRequest.Headers.Referrer = new Uri(initiator);
            using var viewerResponse = await client.SendAsync(viewerRequest);

            Assert.Equal(HttpStatusCode.Redirect, viewerResponse.StatusCode);
            Assert.True(
                viewerResponse.Headers.TryGetValues("Referrer-Policy", out var policies),
                "the redirect carried no Referrer-Policy header");
            Assert.Equal("strict-origin-when-cross-origin", Assert.Single(policies));

            // The browser keeps the initiator's Referer on the follow-up request under that
            // policy; the guard must then accept the gateway's own redirect target.
            using var followUpRequest = new HttpRequestMessage(HttpMethod.Get, viewerResponse.Headers.Location);
            followUpRequest.Headers.Referrer = new Uri(initiator);
            using var followUpResponse = await client.SendAsync(followUpRequest);

            Assert.Equal(HttpStatusCode.OK, followUpResponse.StatusCode);
        }
        finally
        {
            DeleteTempDirectory(distPath);
        }
    }

    [Fact]
    public async Task Viewer_WhenDistPathIsMissing_Returns503()
    {
        // OpenDocViewerIndexRenderer.RenderAsync: no dist path configured. The commit
        // that added the status codes listed this path, but nothing covered it — a
        // later edit could drop the code back to 200 and every test would stay green.
        using var factory = new GatewayFactory(distPath: null, allowFallbackWithoutSession: true);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Theory]
    [InlineData(true, HttpStatusCode.ServiceUnavailable)]
    [InlineData(false, HttpStatusCode.OK)]
    public async Task Viewer_WithoutConfiguredDistPath_FallbackProbingIsGatedByRequireExplicitFlag(
        bool requireExplicitDistPath,
        HttpStatusCode expected)
    {
        // Same layout both times: no configured dist path, but a dist under the content
        // root's wwwroot/odv. RequireExplicitOpenDocViewerDistPath=true must refuse to probe
        // (503); false is the unset-in-production default and must find it (200).
        var contentRoot = CreateContentRootWithFallbackDist();
        try
        {
            using var factory = new GatewayFactory(
                distPath: null,
                allowFallbackWithoutSession: true,
                requireExplicitDistPath: requireExplicitDistPath,
                contentRoot: contentRoot);
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/");

            Assert.Equal(expected, response.StatusCode);
        }
        finally
        {
            DeleteTempDirectory(contentRoot);
        }
    }

    [Fact]
    public async Task Viewer_WhenDistExistsButIndexIsMissing_Returns503()
    {
        // The second renderer path: the dist folder resolves, but index.html cannot
        // be read (MissingIndexPage). A different failure with the same answer.
        var distPath = NewTempDirectoryPath();
        try
        {
            Directory.CreateDirectory(distPath);
            using var factory = new GatewayFactory(distPath, allowFallbackWithoutSession: true);
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/");

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }
        finally
        {
            DeleteTempDirectory(distPath);
        }
    }

    /// <summary>A fresh, unique path under the temp root. Nothing is created yet, so the
    /// caller can create it inside its own try block and clean up in finally.</summary>
    private static string NewTempDirectoryPath()
        => Path.Join(Path.GetTempPath(), "odvgateway-tests-" + Guid.NewGuid().ToString("N"));

    private static string CreateDistDirectory()
    {
        var path = NewTempDirectoryPath();
        Directory.CreateDirectory(path);
        try
        {
            File.WriteAllText(Path.Join(path, "index.html"), "<!doctype html><html></html>");
        }
        catch
        {
            // Do not leak an empty directory when the index cannot be written.
            DeleteTempDirectory(path);
            throw;
        }

        return path;
    }

    /// <summary>A content root whose wwwroot/odv holds a dist, so fallback probing has
    /// something deterministic to find without touching the developer's sibling checkout.</summary>
    private static string CreateContentRootWithFallbackDist()
    {
        var root = NewTempDirectoryPath();
        var dist = Path.Join(root, "wwwroot", "odv");
        Directory.CreateDirectory(dist);
        try
        {
            File.WriteAllText(Path.Join(dist, "index.html"), "<!doctype html><html></html>");
        }
        catch
        {
            DeleteTempDirectory(root);
            throw;
        }

        return root;
    }

    private static void DeleteTempDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static string EncodeBase64Url(string json)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    private sealed class GatewayFactory : WebApplicationFactory<Program>
    {
        private readonly string? _distPath;
        private readonly bool _allowFallbackWithoutSession;
        private readonly bool _requireExplicitDistPath;
        private readonly string? _contentRoot;
        private readonly string? _allowedInitiatorUrl;

        // allowFallbackWithoutSession lets a probe reach OpenDocViewerIndexRenderer
        // without a prepared session. Without it every request to "/" stops at the
        // 400/404 session guard, so the renderer's own 503 paths were unreachable
        // from a test — which is exactly why they were shipped untested.
        public GatewayFactory(
            string? distPath,
            bool allowFallbackWithoutSession = false,
            bool requireExplicitDistPath = true,
            string? contentRoot = null,
            string? allowedInitiatorUrl = null)
        {
            _distPath = distPath;
            _allowFallbackWithoutSession = allowFallbackWithoutSession;
            _requireExplicitDistPath = requireExplicitDistPath;
            _contentRoot = contentRoot;
            _allowedInitiatorUrl = allowedInitiatorUrl;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Neutral environment and explicit overrides keep the probes
            // deterministic: appsettings.Development.json points at a sibling
            // OpenDocViewer checkout that may exist on a developer machine.
            builder.UseEnvironment("Test");
            if (_contentRoot is not null)
            {
                builder.UseContentRoot(_contentRoot);
            }

            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ODVGateway:OpenDocViewerDistPath"] = _distPath ?? string.Empty,
                    ["ODVGateway:RequireExplicitOpenDocViewerDistPath"] = _requireExplicitDistPath ? "true" : "false",
                    ["ODVGateway:AllowOpenDocViewerFallbackWithoutSession"] =
                        _allowFallbackWithoutSession ? "true" : "false",
                    ["ODVGateway:WebClientHandoff:AllowedInitiatorUrls:0"] = _allowedInitiatorUrl
                });
            });
        }
    }
}
