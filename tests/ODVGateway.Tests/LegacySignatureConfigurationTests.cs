using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace ODVGateway.Tests;

public sealed class LegacySignatureConfigurationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LegacySignaturesSection_IsIgnoredAndDocumentsAreStillServed(bool enabled)
    {
        var contentRoot = Path.Join(Path.GetTempPath(), "odvgateway-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contentRoot);
        var documentPath = Path.Join(contentRoot, "document.pdf");
        var documentBytes = "%PDF-1.4\n% legacy configuration compatibility\n"u8.ToArray();
        await File.WriteAllBytesAsync(documentPath, documentBytes, TestContext.Current.CancellationToken);

        try
        {
            using var factory = new LegacyConfigurationFactory(contentRoot, enabled);
            using var client = factory.CreateClient();
            var prepJson = JsonSerializer.Serialize(new
            {
                userId = "test-user",
                sessionId = "test-session",
                portableDocuments = new[]
                {
                    new
                    {
                        documentId = "document-1",
                        fileData = new[] { $"file-1|pdf|{documentPath}" }
                    }
                }
            });
            using var prep = new StringContent(prepJson, Encoding.UTF8, "application/json");
            using var prepared = await client.PostAsync("/prep", prep, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, prepared.StatusCode);
            using var prepPayload = JsonDocument.Parse(
                await prepared.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            var sessionKey = prepPayload.RootElement.GetProperty("sessionKey").GetString();
            Assert.False(string.IsNullOrWhiteSpace(sessionKey));

            using var source = await client.GetAsync($"/source/{sessionKey}/0", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, source.StatusCode);
            Assert.Equal("application/pdf", source.Content.Headers.ContentType?.MediaType);
            Assert.Equal("no-store", source.Headers.CacheControl?.ToString());
            Assert.Equal(documentBytes, await source.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));

            using var removedEndpoint = await client.GetAsync(
                $"/signatures/{sessionKey}/0", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, removedEndpoint.StatusCode);
        }
        finally
        {
            Directory.Delete(contentRoot, recursive: true);
        }
    }

    private sealed class LegacyConfigurationFactory(string contentRoot, bool enabled)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ODVGateway:RequireExplicitOpenDocViewerDistPath"] = "true",
                    ["ODVGateway:OpenDocViewerDistPath"] = string.Empty,
                    ["ODVGateway:TrustClientFilePath"] = "true",
                    ["ODVGateway:TrustedSourceRoots:0"] = contentRoot,
                    ["ODVGateway:signatures:enabled"] = enabled ? "true" : "false",
                    ["ODVGateway:signatures:workerTestMode"] = "true",
                    ["ODVGateway:signatures:workerMaxMemoryBytes"] = "1",
                    ["ODVGateway:signatures:unknownLegacyKey"] = "ignored"
                }));
        }
    }
}
