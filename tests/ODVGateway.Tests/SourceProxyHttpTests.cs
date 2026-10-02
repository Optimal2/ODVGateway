using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ODVGateway.Models;
using ODVGateway.Services;

namespace ODVGateway.Tests;

public sealed class SourceProxyHttpTests
{
    private const int Limit = 128 * 1024;

    [Theory]
    [InlineData(Limit - 1, HttpStatusCode.OK)]
    [InlineData(Limit, HttpStatusCode.OK)]
    [InlineData(Limit + 1, HttpStatusCode.BadGateway)]
    public async Task UnknownLength_ValidatesEntirePayloadBeforeReturningSuccess(int length, HttpStatusCode expected)
    {
        using var factory = new ProxyFactory(length);
        using var client = factory.CreateClient();
        var session = factory.Services.GetRequiredService<GatewaySessionStore>().Store(new WebClientPrepRequest
        {
            UserId = "test-user",
            SessionId = "test-session",
            PortableDocuments = [new WebClientPortableDocument
            {
                DocumentId = "document-1",
                FileData = ["file-1|pdf|"]
            }]
        }).Session!;

        using var response = await client.GetAsync($"/source/{session.SessionKey}/0", TestContext.Current.CancellationToken);
        Assert.Equal(expected, response.StatusCode);
        var body = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        if (expected == HttpStatusCode.OK)
        {
            Assert.Equal(new byte[length], body);
        }
        else
        {
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            Assert.Contains("too large", System.Text.Encoding.UTF8.GetString(body));
        }
        Assert.Equal(1, factory.Handler.RequestCount);
    }

    private sealed class ProxyFactory(int length) : WebApplicationFactory<Program>
    {
        public StubHandler Handler { get; } = new(length);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ODVGateway:RequireExplicitOpenDocViewerDistPath"] = "true",
                    ["ODVGateway:OpenDocViewerDistPath"] = "",
                    ["ODVGateway:MaxSourceProxyBytes"] = Limit.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["ODVGateway:WebClientSourceFallback:ProxyThroughGatewayAboveSourceCount"] = "0"
                }));
            builder.ConfigureServices(services => services.AddHttpClient("ODVGateway.RemoteInline")
                .ConfigurePrimaryHttpMessageHandler(() => Handler));
        }
    }

    private sealed class StubHandler(int length) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new UnknownLengthStream(new byte[length]))
            });
        }
    }

    // Multiple reads and no Content-Length reproduce a chunked upstream response.
    private sealed class UnknownLengthStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(buffer.Length, 16 * 1024)], cancellationToken);
    }
}
