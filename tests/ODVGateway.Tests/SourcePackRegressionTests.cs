using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ODVGateway.Models;
using ODVGateway.Options;
using ODVGateway.Services;

namespace ODVGateway.Tests;

public sealed class SourcePackRegressionTests
{
    [Fact]
    public async Task KnownLengthStream_WhenTruncated_ThrowsInsteadOfReturningShortPayload()
    {
        var method = FindProgramMethod("ReadSourcePackBytesWithLimitAsync");
        using var source = new MemoryStream([1, 2, 3]);

        var invocation = method.Invoke(null, [source, 1024L, 8L, 128, CancellationToken.None]);
        var readTask = Assert.IsType<Task<byte[]>>(invocation);

        await Assert.ThrowsAsync<EndOfStreamException>(async () => await readTask);
    }

    [Fact]
    public async Task DirectSource_IsFullyReadBeforePayloadIsReturned()
    {
        var root = Path.Join(Path.GetTempPath(), "odvgateway-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var filePath = Path.Join(root, "source.pdf");
        var expected = new byte[] { 1, 2, 3, 4, 5 };
        File.WriteAllBytes(filePath, expected);

        try
        {
            var options = new ODVGatewayOptions
            {
                TrustClientFilePath = true,
                TrustedSourceRoots = [root]
            };
            var monitor = new StaticOptionsMonitor<ODVGatewayOptions>(options);
            var source = CreateSource(filePath);
            var session = CreateSession(source);
            var arguments = new Dictionary<Type, object?>
            {
                [typeof(HttpRequest)] = new DefaultHttpContext().Request,
                [typeof(GatewaySession)] = session,
                [typeof(GatewaySourceFile)] = source,
                [typeof(ContentTypeMapper)] = new ContentTypeMapper(Microsoft.Extensions.Options.Options.Create(options)),
                [typeof(ODVGatewayOptions)] = options,
                [typeof(DirectSourceFileResolver)] = new DirectSourceFileResolver(
                    monitor, NullLogger<DirectSourceFileResolver>.Instance),
                [typeof(IHttpClientFactory)] = new StubHttpClientFactory(new NotUsedHandler()),
                [typeof(WebClientFallbackUrlBuilder)] = new WebClientFallbackUrlBuilder(),
                [typeof(WebClientSourceProxyLimiter)] = new WebClientSourceProxyLimiter(monitor),
                [typeof(ILogger)] = NullLogger.Instance,
                [typeof(CancellationToken)] = CancellationToken.None
            };

            var payload = await InvokeWithArguments<SourcePackPayload>("ReadGatewaySourceBytesAsync", arguments);

            Assert.True(payload.Ok);
            Assert.Equal(expected, payload.Bytes);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task OversizedRemotePayload_LogsAttemptAndSafeSourceMarker()
    {
        var options = new ODVGatewayOptions
        {
            MaxSourcePackFrameBytes = 4,
            RemoteInlineSources = new RemoteInlineSourceOptions { RetryCount = 0 }
        };
        var monitor = new StaticOptionsMonitor<ODVGatewayOptions>(options);
        var source = CreateSource(filePath: "");
        var session = CreateSession(source);
        var logger = new CaptureLogger();
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new MemoryStream(new byte[8]))
        };
        response.Content.Headers.ContentLength = null;
        var arguments = new Dictionary<Type, object?>
        {
            [typeof(GatewaySession)] = session,
            [typeof(GatewaySourceFile)] = source,
            [typeof(string)] = "https://webclient.example/source",
            [typeof(ContentTypeMapper)] = new ContentTypeMapper(Microsoft.Extensions.Options.Options.Create(options)),
            [typeof(ODVGatewayOptions)] = options,
            [typeof(IHttpClientFactory)] = new StubHttpClientFactory(new SingleResponseHandler(response)),
            [typeof(WebClientSourceProxyLimiter)] = new WebClientSourceProxyLimiter(monitor),
            [typeof(ILogger)] = logger,
            [typeof(CancellationToken)] = CancellationToken.None
        };

        var payload = await InvokeWithArguments<SourcePackPayload>("FetchWebClientSourceBytesAsync", arguments);

        Assert.False(payload.Ok);
        Assert.Contains(logger.Messages, message =>
            message.Contains("Attempt=1", StringComparison.Ordinal) &&
            message.Contains("Source=configured-webclient-source", StringComparison.Ordinal));
    }

    private static GatewaySourceFile CreateSource(string filePath)
    {
        return new GatewaySourceFile
        {
            Index = 0,
            DocumentId = "document-1",
            DocumentIndex = 0,
            FileNumber = 1,
            FileId = "file-1",
            Extension = "pdf",
            FilePath = filePath,
            DisplayName = "source.pdf"
        };
    }

    private static GatewaySession CreateSession(GatewaySourceFile source)
    {
        return new GatewaySession
        {
            SessionKey = "session-key",
            HandoffLookupKey = "lookup-key",
            Prep = new WebClientPrepRequest
            {
                UserId = "user",
                SessionId = "session",
                PortableDocuments = [new WebClientPortableDocument { DocumentId = "document-1" }]
            },
            CreatedUtc = DateTimeOffset.UtcNow,
            ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(5),
            SourceFiles = [source]
        };
    }

    private static async Task<T> InvokeWithArguments<T>(
        string methodName,
        IReadOnlyDictionary<Type, object?> arguments)
    {
        var method = FindProgramMethod(methodName);
        var parameters = method.GetParameters()
            .Select(parameter => arguments.TryGetValue(parameter.ParameterType, out var value)
                ? value
                : throw new InvalidOperationException($"No test argument for {parameter.ParameterType}."))
            .ToArray();
        var invocation = method.Invoke(null, parameters);
        return await Assert.IsAssignableFrom<Task<T>>(invocation);
    }

    private static MethodInfo FindProgramMethod(string methodName)
    {
        return typeof(Program).GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Single(method => method.Name.Contains(methodName, StringComparison.Ordinal));
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class NotUsedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("The direct-file test must not use HTTP.");
    }

    private sealed class SingleResponseHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(response);
    }

    private sealed class CaptureLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }
}
