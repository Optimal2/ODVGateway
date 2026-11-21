using System.Net;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ODVGateway.Models;
using ODVGateway.Services;

namespace ODVGateway.Tests;

// HTTP-level probes for the status codes the gateway actually returns. A monitor
// reads the status line, not the payload, so /health must answer 503 when the
// payload says "degraded", and the error pages must carry their real 4xx codes.
// The factory boots the real app on the in-memory TestServer; the only
// filesystem touches are throwaway dist folders for the renderer probes.
public sealed class GatewayHttpStatusTests
{
    [Theory]
    [InlineData("/")]
    [InlineData("/index.html")]
    public async Task Viewer_WithPhysicalIndex_StillRequiresSession(string path)
    {
        var distPath = CreateDistDirectory();
        try
        {
            using var factory = new GatewayFactory(distPath);
            using var client = factory.CreateClient();
            using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("no WebClient sessiondata", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            DeleteTempDirectory(distPath);
        }
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/index.html")]
    public async Task Viewer_WithPhysicalIndex_InjectsPreparedBundle(string path)
    {
        var distPath = CreateDistDirectory();
        try
        {
            using var factory = new GatewayFactory(distPath, useBundleUrlHandoff: false);
            using var client = factory.CreateClient();
            using var prep = new StringContent(
                """{"userId":"u1","sessionId":"s1","portableDocuments":[{"documentId":"d1","fileData":[]}]}""",
                Encoding.UTF8, "application/json");
            using var prepared = await client.PostAsync("/prep", prep, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, prepared.StatusCode);
            var sessionData = EncodeBase64Url("""{"userId":"u1","sessionId":"s1","caseIds":["d1"]}""");
            using var response = await client.GetAsync(path + "?sessiondata=" + sessionData, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            Assert.Contains("odvgateway-bootstrap", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            DeleteTempDirectory(distPath);
        }
    }

    [Fact]
    public async Task Health_DistUnavailable_Returns503WithDegradedPayload()
    {
        using var factory = new GatewayFactory(distPath: null);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);
        var payload = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

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

            using var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);
            var payload = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

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

        using var response = await client.GetAsync("/", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("dark", "dark", "dark")]
    [InlineData("light", "light", "light")]
    [InlineData("system", "light", "system")]
    public async Task StatusPage_FollowsTheSharedThemePreferenceCookie(
        string mode,
        string expectedTheme,
        string expectedMode)
    {
        // The OMP theme contract: OMP_THEME_PREFERENCE carries URI-encoded JSON
        // {"version":1,"mode":...}; the server-rendered status page stamps
        // data-theme/data-theme-mode on <html>. "system" falls back to the light
        // palette server-side; CSS follows prefers-color-scheme.
        using var factory = new GatewayFactory(distPath: null);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.TryAddWithoutValidation("Cookie", ThemeCookie(mode));

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains($"data-theme=\"{expectedTheme}\" data-theme-mode=\"{expectedMode}\"", html);
    }

    [Theory]
    [InlineData("gibberish")]
    [InlineData("{\"version\":1,\"mode\":\"\\uD800\"}")]
    [InlineData("{\"version\":1,\"mode\":\"\\uDC00\"}")]
    public async Task StatusPage_InvalidThemeCookie_FallsBackToSystem(string cookie)
    {
        using var factory = new GatewayFactory(distPath: null);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.TryAddWithoutValidation("Cookie", "OMP_THEME_PREFERENCE=" + Uri.EscapeDataString(cookie));

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("data-theme=\"light\" data-theme-mode=\"system\"", html);
    }

    [Fact]
    public async Task StatusPage_UnknownCookieVersion_FallsBackToSystem()
    {
        using var factory = new GatewayFactory(distPath: null);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.TryAddWithoutValidation(
            "Cookie",
            "OMP_THEME_PREFERENCE=" + Uri.EscapeDataString("""{"version":2,"mode":"dark","revision":"r1"}"""));

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("data-theme=\"light\" data-theme-mode=\"system\"", html);
    }

    [Fact]
    public async Task StatusPage_CarriesSystemMediaQueryAndLightPrintFallback()
    {
        // Without a cookie the page defaults to system mode: dark must come from
        // prefers-color-scheme, and print must always use the light palette.
        using var factory = new GatewayFactory(distPath: null);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/", TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("prefers-color-scheme: dark", html);
        Assert.Contains("@media print", html);
    }

    [Fact]
    public async Task StatusPage_CspStillAllowsInlinePageAssetsAndPrintBlobs()
    {
        // The status pages use an inline <style>, the renderer injects an inline
        // bootstrap <script>, and PDF printing needs blob: in frame-src/connect-src.
        // A CSP edit that drops any of these breaks the pages or printing silently.
        using var factory = new GatewayFactory(distPath: null);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/", TestContext.Current.CancellationToken);

        Assert.True(response.Headers.TryGetValues("Content-Security-Policy", out var values));
        var csp = Assert.Single(values);
        Assert.Contains("script-src 'self' 'unsafe-inline'", csp);
        Assert.Contains("'wasm-unsafe-eval'", csp);
        Assert.DoesNotContain("'unsafe-eval'", csp);
        Assert.Contains("style-src 'self' 'unsafe-inline'", csp);
        Assert.Contains("frame-src 'self' blob:", csp);
        Assert.Contains("connect-src 'self' blob:", csp);
    }

    [Fact]
    public async Task StatusPage_SendsVaryCookieAndNoStore()
    {
        // The status pages vary with the OMP_THEME_PREFERENCE cookie, so
        // caches must key on Cookie and must not store the page at all.
        using var factory = new GatewayFactory(distPath: null);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.True(response.Headers.TryGetValues("Vary", out var vary));
        Assert.Contains(vary, value => value.Contains("Cookie", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Viewer_WithUndecodableSessionData_Returns400HtmlStatusPage()
    {
        // The viewer flow is browser-facing: like the missing-sessiondata
        // page, an undecodable token answers with a themed HTML status page,
        // not a bare JSON string.
        using var factory = new GatewayFactory(distPath: null);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/?sessiondata=!!!not-base64!!!", TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("could not be decoded", html);
    }

    [Fact]
    public async Task Viewer_WithRejectedHandoff_Returns403HtmlStatusPage()
    {
        // Same for a rejected hand-over: the viewer answers with the themed
        // page that carries the reason, not a bare 403.
        using var factory = new GatewayFactory(distPath: null, allowedInitiatorUrl: "/WebClientODV/DocumentView");
        using var client = factory.CreateClient();
        var sessionData = EncodeBase64Url("""{"userId":"u1","sessionId":"s1"}""");

        using var response = await client.GetAsync(
            "/?sessiondata=" + sessionData, TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("handoff", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Health_DefaultConfiguration_ProxyCapFollowsFrameCap()
    {
        // maxSourceProxyBytes 0 means "follow maxSourcePackFrameBytes"
        // (GetMaxSourceProxyBytes); /health must echo them as equal.
        using var factory = new GatewayFactory(distPath: null);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);
        var payload = JsonNode.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!.AsObject();

        Assert.Equal(
            payload["maxSourcePackFrameBytes"]!.GetValue<long>(),
            payload["maxSourceProxyBytes"]!.GetValue<long>());
    }

    [Fact]
    public async Task Source_WhenDirectFileCannotBeRead_Returns403()
    {
        // FileInfo.Exists succeeds on attributes alone (a data-read denial
        // still resolves), so the /source endpoint must map the refused
        // OpenRead to 403 instead of a generic 500. The data-read denial
        // needs an ACL, so this probe is Windows-only.
        if (!OperatingSystem.IsWindows())
        {
            TestContext.Current.SendDiagnosticMessage("Skipped: the read-deny ACL probe is Windows-only.");
            return;
        }

        var root = NewTempDirectoryPath();
        Directory.CreateDirectory(root);
        var filePath = Path.Join(root, "denied.pdf");
        File.WriteAllBytes(filePath, [1, 2, 3]);
        DenyReadData(filePath);
        try
        {
            using var factory = new GatewayFactory(
                distPath: null, trustClientFilePath: true, trustedSourceRoot: root);
            var session = factory.Services.GetRequiredService<GatewaySessionStore>().Store(new WebClientPrepRequest
            {
                UserId = "u1",
                SessionId = "s1",
                PortableDocuments = [new WebClientPortableDocument
                {
                    DocumentId = "d1",
                    FileData = ["f1|pdf|" + filePath]
                }]
            }).Session!;
            using var client = factory.CreateClient();

            using var response = await client.GetAsync(
                $"/source/{Uri.EscapeDataString(session.SessionKey)}/0",
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        finally
        {
            AllowReadData(filePath);
            DeleteTempDirectory(root);
        }
    }

    // The ACL probe below is Windows-only; the caller returns early on other
    // platforms before touching it.
#pragma warning disable CA1416
    private static void DenyReadData(string path)
    {
        var info = new FileInfo(path);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.ReadData,
            AccessControlType.Deny));
        info.SetAccessControl(security);
    }

    private static void AllowReadData(string path)
    {
        var info = new FileInfo(path);
        var security = info.GetAccessControl();
        security.RemoveAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.ReadData,
            AccessControlType.Deny));
        info.SetAccessControl(security);
    }
#pragma warning restore CA1416

    private static string ThemeCookie(string mode)
    {
        return "OMP_THEME_PREFERENCE="
            + Uri.EscapeDataString($$"""{"version":1,"mode":"{{mode}}","revision":"mabc123"}""");
    }

    [Fact]
    public async Task Viewer_WithoutPreparedSession_Returns404()
    {
        using var factory = new GatewayFactory(distPath: null);
        using var client = factory.CreateClient();
        var sessionData = EncodeBase64Url("""{"userId":"u1","sessionId":"s1"}""");

        using var response = await client.GetAsync("/?sessiondata=" + sessionData, TestContext.Current.CancellationToken);

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
            using var prepResponse = await client.SendAsync(prepRequest, TestContext.Current.CancellationToken);
            Assert.True(prepResponse.IsSuccessStatusCode, $"/prep answered {(int)prepResponse.StatusCode}");

            var sessionData = EncodeBase64Url("""{"userId":"u1","sessionId":"s1","aspxAuth":"a1","caseIds":["d1"]}""");
            using var viewerRequest = new HttpRequestMessage(HttpMethod.Get, "/?sessiondata=" + sessionData);
            viewerRequest.Headers.Referrer = new Uri(initiator);
            using var viewerResponse = await client.SendAsync(viewerRequest, TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.Redirect, viewerResponse.StatusCode);
            Assert.True(
                viewerResponse.Headers.TryGetValues("Referrer-Policy", out var policies),
                "the redirect carried no Referrer-Policy header");
            Assert.Equal("strict-origin-when-cross-origin", Assert.Single(policies));

            // The browser keeps the initiator's Referer on the follow-up request under that
            // policy; the guard must then accept the gateway's own redirect target.
            using var followUpRequest = new HttpRequestMessage(HttpMethod.Get, viewerResponse.Headers.Location);
            followUpRequest.Headers.Referrer = new Uri(initiator);
            using var followUpResponse = await client.SendAsync(followUpRequest, TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, followUpResponse.StatusCode);

            // Negative edge (review finding 2026-09-10): without it the assertion above would
            // also pass if the allowlist were never bound, because an empty allowlist lets
            // everything through. The same follow-up without Referer must still be rejected.
            using var blindFollowUpRequest = new HttpRequestMessage(HttpMethod.Get, viewerResponse.Headers.Location);
            using var blindFollowUpResponse = await client.SendAsync(blindFollowUpRequest, TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.Forbidden, blindFollowUpResponse.StatusCode);
        }
        finally
        {
            DeleteTempDirectory(distPath);
        }
    }

    [Fact]
    public async Task Viewer_WhenDistPathIsMissing_Returns503WithDarkTheme()
    {
        // OpenDocViewerIndexRenderer.RenderAsync: no dist path configured. The commit
        // that added the status codes listed this path, but nothing covered it — a
        // later edit could drop the code back to 200 and every test would stay green.
        using var factory = new GatewayFactory(distPath: null, allowFallbackWithoutSession: true);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.TryAddWithoutValidation("Cookie", ThemeCookie("dark"));

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("OpenDocViewer dist folder was not found", html);
        Assert.Contains("data-theme=\"dark\" data-theme-mode=\"dark\"", html);
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

            using var response = await client.GetAsync("/", TestContext.Current.CancellationToken);

            Assert.Equal(expected, response.StatusCode);
        }
        finally
        {
            DeleteTempDirectory(contentRoot);
        }
    }

    [Fact]
    public async Task Viewer_WhenDistExistsButIndexIsMissing_Returns503WithDarkTheme()
    {
        // The resolver rejects a folder without index.html, so this reaches the
        // missing-dist page, not the renderer's index-read failure page.
        var distPath = NewTempDirectoryPath();
        try
        {
            Directory.CreateDirectory(distPath);
            using var factory = new GatewayFactory(distPath, allowFallbackWithoutSession: true);
            using var client = factory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, "/");
            request.Headers.TryAddWithoutValidation("Cookie", ThemeCookie("dark"));

            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
            Assert.Contains("OpenDocViewer dist folder was not found", html);
            Assert.Contains("data-theme=\"dark\" data-theme-mode=\"dark\"", html);
        }
        finally
        {
            DeleteTempDirectory(distPath);
        }
    }

    [Fact]
    public async Task Viewer_WhenIndexCannotBeRead_Returns503WithDarkTheme()
    {
        var distPath = CreateDistDirectory();
        try
        {
            using var factory = new GatewayFactory(distPath, allowFallbackWithoutSession: true);
            using var client = factory.CreateClient();
            // Keep the index present for the resolver but unreadable for the renderer.
            // This exercises MissingIndexPage without a timing-dependent deletion race.
            using var lockedIndex = new FileStream(
                Path.Join(distPath, "index.html"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            using var request = new HttpRequestMessage(HttpMethod.Get, "/");
            request.Headers.TryAddWithoutValidation("Cookie", ThemeCookie("dark"));

            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
            Assert.Contains("OpenDocViewer index.html was not found", html);
            Assert.Contains("data-theme=\"dark\" data-theme-mode=\"dark\"", html);
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
        private readonly bool _useBundleUrlHandoff;
        private readonly bool _trustClientFilePath;
        private readonly string? _trustedSourceRoot;

        // allowFallbackWithoutSession lets a probe reach OpenDocViewerIndexRenderer
        // without a prepared session. Without it every request to "/" stops at the
        // 400/404 session guard, so the renderer's own 503 paths were unreachable
        // from a test — which is exactly why they were shipped untested.
        public GatewayFactory(
            string? distPath,
            bool allowFallbackWithoutSession = false,
            bool requireExplicitDistPath = true,
            string? contentRoot = null,
            string? allowedInitiatorUrl = null,
            bool useBundleUrlHandoff = true,
            bool trustClientFilePath = false,
            string? trustedSourceRoot = null)
        {
            _distPath = distPath;
            _allowFallbackWithoutSession = allowFallbackWithoutSession;
            _requireExplicitDistPath = requireExplicitDistPath;
            _contentRoot = contentRoot;
            _allowedInitiatorUrl = allowedInitiatorUrl;
            _useBundleUrlHandoff = useBundleUrlHandoff;
            _trustClientFilePath = trustClientFilePath;
            _trustedSourceRoot = trustedSourceRoot;
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
                var overrides = new Dictionary<string, string?>
                {
                    ["ODVGateway:OpenDocViewerDistPath"] = _distPath ?? string.Empty,
                    ["ODVGateway:RequireExplicitOpenDocViewerDistPath"] = _requireExplicitDistPath ? "true" : "false",
                    ["ODVGateway:AllowOpenDocViewerFallbackWithoutSession"] =
                        _allowFallbackWithoutSession ? "true" : "false",
                    ["ODVGateway:WebClientHandoff:AllowedInitiatorUrls:0"] = _allowedInitiatorUrl,
                    ["ODVGateway:UseBundleUrlHandoff"] = _useBundleUrlHandoff ? "true" : "false",
                    ["ODVGateway:TrustClientFilePath"] = _trustClientFilePath ? "true" : "false"
                };
                if (_trustedSourceRoot is not null)
                {
                    overrides["ODVGateway:TrustedSourceRoots:0"] = _trustedSourceRoot;
                }

                configuration.AddInMemoryCollection(overrides);
            });
        }
    }
}
