using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace ODVGateway.Tests;

public sealed class StaticFileCacheTests
{
    [Theory]
    [InlineData("odv.config.js")]
    [InlineData("odv.site.config.js")]
    public async Task MutableDistFiles_AreNotCached(string fileName)
    {
        await AssertCacheControl(fileName, "no-store");
    }

    [Fact]
    public async Task HashedDistAsset_RemainsImmutable()
    {
        await AssertCacheControl("assets/app.abc123.js", "public, max-age=31536000, immutable");
    }

    private static async Task AssertCacheControl(string relativePath, string expected)
    {
        var distPath = Path.Join(Path.GetTempPath(), "odvgateway-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Join(distPath, "assets"));
        File.WriteAllText(Path.Join(distPath, "index.html"), "<!doctype html><html></html>");
        File.WriteAllText(Path.Join(distPath, "odv.config.js"), "window.odvConfig = {};");
        File.WriteAllText(Path.Join(distPath, "odv.site.config.js"), "window.odvSiteConfig = {};");
        File.WriteAllText(Path.Join(distPath, "assets", "app.abc123.js"), "console.log('asset');");

        try
        {
            using var factory = new StaticFileFactory(distPath);
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/" + relativePath, TestContext.Current.CancellationToken);

            Assert.True(response.IsSuccessStatusCode);
            Assert.Equal(expected, response.Headers.CacheControl?.ToString());
        }
        finally
        {
            Directory.Delete(distPath, recursive: true);
        }
    }

    private sealed class StaticFileFactory(string distPath) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ODVGateway:OpenDocViewerDistPath"] = distPath,
                    ["ODVGateway:RequireExplicitOpenDocViewerDistPath"] = "true"
                }));
        }
    }
}
