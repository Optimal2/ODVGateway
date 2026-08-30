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
            Directory.Delete(distPath, recursive: true);
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

    [Fact]
    public async Task Viewer_WhenDistExistsButIndexIsMissing_Returns503()
    {
        // The second renderer path: the dist folder resolves, but index.html cannot
        // be read (MissingIndexPage). A different failure with the same answer.
        var distPath = Path.Join(Path.GetTempPath(), "odvgateway-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(distPath);
        try
        {
            using var factory = new GatewayFactory(distPath, allowFallbackWithoutSession: true);
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/");

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }
        finally
        {
            Directory.Delete(distPath, recursive: true);
        }
    }

    private static string CreateDistDirectory()
    {
        var path = Path.Join(Path.GetTempPath(), "odvgateway-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Join(path, "index.html"), "<!doctype html><html></html>");
        return path;
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

        // allowFallbackWithoutSession lets a probe reach OpenDocViewerIndexRenderer
        // without a prepared session. Without it every request to "/" stops at the
        // 400/404 session guard, so the renderer's own 503 paths were unreachable
        // from a test — which is exactly why they were shipped untested.
        public GatewayFactory(string? distPath, bool allowFallbackWithoutSession = false)
        {
            _distPath = distPath;
            _allowFallbackWithoutSession = allowFallbackWithoutSession;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Neutral environment and explicit overrides keep the probes
            // deterministic: appsettings.Development.json points at a sibling
            // OpenDocViewer checkout that may exist on a developer machine.
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ODVGateway:OpenDocViewerDistPath"] = _distPath ?? string.Empty,
                    ["ODVGateway:RequireExplicitOpenDocViewerDistPath"] = "true",
                    ["ODVGateway:AllowOpenDocViewerFallbackWithoutSession"] =
                        _allowFallbackWithoutSession ? "true" : "false"
                });
            });
        }
    }
}
