using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace ODVGateway.Tests;

/// <summary>
/// Focused behavioral probes for the startup fixture's resource ownership and exception boundaries.
/// These use controlled handlers, tasks, and an unstarted owned process; they do not start another
/// gateway or touch any external process.
/// </summary>
public sealed partial class LegacySignatureStartupTests
{
    [Fact]
    public async Task PrepRequestContent_IsDisposedAfterPost()
    {
        HttpContent? sentContent = null;
        TrackingHttpResponseMessage? handlerResponse = null;
        using var handler = new DelegateHttpHandler((request, _) =>
        {
            sentContent = request.Content;
            handlerResponse = CreateControlledResponse("{}");
            return Task.FromResult<HttpResponseMessage>(handlerResponse);
        });
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://unit.test") };

        var response = await PostPrepAsync(
            client, new JsonObject { ["sessionId"] = "owned-content" }, TestContext.Current.CancellationToken);

        Assert.NotNull(sentContent);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await sentContent.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var trackedResponse = Assert.IsType<TrackingHttpResponseMessage>(response);
        Assert.Same(handlerResponse, trackedResponse);
        Assert.False(trackedResponse.IsDisposed);

        client.Dispose();

        Assert.False(trackedResponse.IsDisposed);
        Assert.Equal("{}", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        response.Dispose();
        Assert.True(trackedResponse.IsDisposed);
    }

    [Fact]
    public void FreeLoopbackPort_CanBeReboundAfterProbe()
    {
        var port = GetFreeLoopbackPort();

        using var listener = new TcpListener(IPAddress.Loopback, port) { ExclusiveAddressUse = true };
        Assert.Null(Record.Exception(() => listener.Start()));

        Assert.Equal(port, ((IPEndPoint)listener.LocalEndpoint).Port);
    }

    [Fact]
    public async Task MalformedHealthJson_IsDiagnosticAndNotChild()
    {
        TrackingHttpResponseMessage? handlerResponse = null;
        using var handler = new DelegateHttpHandler((request, _) =>
        {
            handlerResponse = CreateControlledResponse(
                "not-json",
                request.Headers.TryGetValues(CorrelationIdHeader, out var markers) ? markers : null);
            return Task.FromResult<HttpResponseMessage>(handlerResponse);
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://unit.test") };
        var diagnostics = new List<string>();

        var probe = await ProbeHealthAsync(
            client, identityMarker: 1234, TestContext.Current.CancellationToken, diagnostics.Add);

        Assert.Equal(HttpStatusCode.OK, probe.Status);
        Assert.Null(probe.StatusValue);
        Assert.False(probe.FromChild);
        var diagnostic = Assert.Single(diagnostics);
        Assert.StartsWith("Ignored malformed /health JSON: ", diagnostic, StringComparison.Ordinal);
        Assert.NotNull(handlerResponse);
        Assert.True(handlerResponse.IsDisposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AbandonedProbeExpectedFailures_AreDiagnostic(bool cancellation)
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var failure = new HttpRequestException("controlled abandoned probe failure");
        var health = cancellation
            ? Task.FromCanceled<HealthProbe>(cancelled.Token)
            : Task.FromException<HealthProbe>(failure);
        var diagnostics = new List<string>();

        await ObserveAbandonedProbeAsync(health, diagnostics.Add);

        var diagnostic = Assert.Single(diagnostics);
        Assert.StartsWith(
            "The abandoned /health probe ended after its gateway child exited: ",
            diagnostic,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AbandonedProbeUnexpectedFailure_Propagates()
    {
        var failure = new InvalidOperationException("unexpected controlled failure");
        var health = Task.FromException<HealthProbe>(failure);
        var diagnostics = new List<string>();

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ObserveAbandonedProbeAsync(health, diagnostics.Add));

        Assert.Same(failure, actual);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void UnstartedOwnedProcess_CleanupReportsKillAndWaitErrors()
    {
        using var process = new Process();
        var diagnostics = new List<string>();

        KillAndDrain(process, diagnostics.Add);

        Assert.Equal(2, diagnostics.Count);
        Assert.Contains(diagnostics, message => message.StartsWith(
            "Could not kill the gateway child during cleanup: ", StringComparison.Ordinal));
        Assert.Contains(diagnostics, message => message.StartsWith(
            "Could not wait for the gateway child during cleanup: ", StringComparison.Ordinal));
    }

    private static async Task<HttpResponseMessage> PostPrepAsync(
        HttpClient client, JsonObject prep, CancellationToken cancellationToken)
    {
        using var prepContent = new StringContent(prep.ToJsonString(), Encoding.UTF8, "application/json");
        return await client.PostAsync("/prep", prepContent, cancellationToken);
    }

    private static TrackingHttpResponseMessage CreateControlledResponse(
        string body, IEnumerable<string>? correlationMarkers = null)
    {
        // The handler transfers response ownership to its caller: PostPrepAsync returns it to the
        // test, while ProbeHealthAsync consumes and disposes it. Neither the factory nor the handler
        // may dispose a response that escapes through the handler.
        var response = new TrackingHttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        if (correlationMarkers is not null)
        {
            response.Headers.Add(CorrelationIdHeader, correlationMarkers);
        }

        return response;
    }

    private sealed class DelegateHttpHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => handler(request, cancellationToken);
    }

    private sealed class TrackingHttpResponseMessage(HttpStatusCode statusCode) : HttpResponseMessage(statusCode)
    {
        public bool IsDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                IsDisposed = true;
            }

            base.Dispose(disposing);
        }
    }
}
