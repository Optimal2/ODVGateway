using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ODVGateway.Models;
using ODVGateway.Services;

namespace ODVGateway.Tests;

public sealed class SourcePackConcurrencyTests
{
    [Fact]
    public async Task SourcePackRemoteFetches_ShareTheConfiguredProxyConcurrencyLimit()
    {
        using var factory = new SourcePackFactory();
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

        var firstRequest = client.GetAsync($"/source-pack/{session.SessionKey}", TestContext.Current.CancellationToken);
        Task<HttpResponseMessage>? secondRequest = null;
        try
        {
            await factory.Handler.FirstRequestEntered.Task.WaitAsync(
                TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            secondRequest = client.GetAsync($"/source-pack/{session.SessionKey}", TestContext.Current.CancellationToken);

            await Task.Delay(250, TestContext.Current.CancellationToken);
            Assert.Equal(1, factory.Handler.RequestCount);
        }
        finally
        {
            factory.Handler.ReleaseRequests.TrySetResult();
        }

        using var firstResponse = await firstRequest;
        Assert.NotNull(secondRequest);
        using var secondResponse = await secondRequest;
        Assert.True(firstResponse.IsSuccessStatusCode);
        Assert.True(secondResponse.IsSuccessStatusCode);
        Assert.Equal(2, factory.Handler.RequestCount);
    }

    private sealed class SourcePackFactory : WebApplicationFactory<Program>
    {
        public BlockingHandler Handler { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ODVGateway:RequireExplicitOpenDocViewerDistPath"] = "true",
                    ["ODVGateway:OpenDocViewerDistPath"] = "",
                    ["ODVGateway:WebClientSourceFallback:ProxyMaxConcurrency"] = "1",
                    ["ODVGateway:RemoteInlineSources:RetryCount"] = "0"
                }));
            builder.ConfigureServices(services => services.AddHttpClient("ODVGateway.RemoteInline")
                .ConfigurePrimaryHttpMessageHandler(() => Handler));
        }
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        private int _requestCount;

        public TaskCompletionSource FirstRequestEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseRequests { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            FirstRequestEntered.TrySetResult();
            await ReleaseRequests.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3, 4])
            };
        }
    }
}
