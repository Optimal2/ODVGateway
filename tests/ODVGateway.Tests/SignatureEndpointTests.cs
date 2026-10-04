using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ODVGateway.Models;
using ODVGateway.Services;
using ODVGateway.Services.Signatures;
using ODVGateway.Tests.Signatures;

namespace ODVGateway.Tests;

/// <summary>
/// HTTP behaviour of <c>GET /signatures/{sessionKey}/{fileIndex}</c>: the switch, the session and
/// index guards shared with <c>/source</c>, the size and format limits, and the JSON contract on the
/// happy path. These probes run the real application on the in-memory TestServer; the only
/// filesystem use is a throwaway fixture directory, which is what the gateway's file source needs.
/// Every test runs in both validation modes: isolated worker (the default) and in-process.
/// </summary>
public sealed class SignatureEndpointTests : IDisposable
{
    private readonly SignatureFixtures _fixtures = new();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task N3_Saturation_Returns503AndReleasesPermitAfterError(bool isolateProcess)
    {
        using var factory = new SignatureFactory(_fixtures, isolateProcess: isolateProcess);
        using var client = factory.CreateClient();
        var sessionKey = PrepareSession(factory, _fixtures.CreateNonPdf("broken.pdf", 4096));
        var limiter = factory.Services.GetRequiredService<SignatureValidationLimiter>();
        using (var held = limiter.TryAcquire())
        {
            Assert.True(held.IsAcquired);
            using var response = await client.GetAsync($"/signatures/{sessionKey}/0", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal(TimeSpan.FromSeconds(1), response.Headers.RetryAfter?.Delta);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        }
        using var failed = await client.GetAsync($"/signatures/{sessionKey}/0", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, failed.StatusCode);
        using var available = limiter.TryAcquire();
        Assert.True(available.IsAcquired);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Disabled_ReturnsNotFoundWithClearReason(bool isolateProcess)
    {
        using var factory = new SignatureFactory(_fixtures, enabled: false, isolateProcess: isolateProcess);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/signatures/any-session/0", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("not enabled", body);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnknownSession_ReturnsNotFound(bool isolateProcess)
    {
        using var factory = new SignatureFactory(_fixtures, isolateProcess: isolateProcess);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/signatures/not-a-session-key/0", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("session was not found", body);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IndexOutsidePreparedSession_ReturnsNotFound(bool isolateProcess)
    {
        using var factory = new SignatureFactory(_fixtures, isolateProcess: isolateProcess);
        using var client = factory.CreateClient();
        var sessionKey = PrepareSession(factory, _fixtures.CreateSignedPdf(new SignatureFixtures.SignedPdfRequest()));

        using var response = await client.GetAsync($"/signatures/{sessionKey}/7", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("outside the prepared session", body);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SignedPdf_ReturnsVerdictsAndNoStoreCaching(bool isolateProcess)
    {
        _fixtures.WriteRootAnchor();
        _fixtures.WriteRootCrl([]);
        var path = _fixtures.CreateSignedPdf(new SignatureFixtures.SignedPdfRequest());
        using var factory = new SignatureFactory(_fixtures, isolateProcess: isolateProcess);
        using var client = factory.CreateClient();
        var sessionKey = PrepareSession(factory, path);

        using var response = await client.GetAsync($"/signatures/{sessionKey}/0", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        // The contract is fixed on the wire spelling, not on C# names.
        Assert.Contains("\"signatures\":[", body);
        Assert.Contains("\"fieldName\":\"Signature1\"", body);
        Assert.Contains("\"kind\":\"approval\"", body);
        Assert.Contains("\"subFilter\":\"adbe.pkcs7.detached\"", body);
        Assert.Contains("\"integrity\":\"intact\"", body);
        Assert.Contains("\"trust\":\"valid\"", body);
        Assert.Contains("\"signingTimeSource\":\"signed-attribute\"", body);
        Assert.Contains("\"coversWholeFile\":true", body);
        Assert.Contains("\"validatedAt\":", body);
        Assert.Contains("\"diagnostics\":[]", body);
        Assert.Contains("\"validationTime\":", body);
        Assert.Contains("\"signerOrganization\":\"Test Unit AB\"", body);

        var parsed = System.Text.Json.JsonSerializer.Deserialize<PdfSignatureValidationResponse>(
            body, SignatureValidationJson.Options);
        Assert.NotNull(parsed);
        Assert.Single(parsed.Signatures);
        Assert.Equal(PdfSignatureTrust.Valid, parsed.Signatures[0].Trust);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BrokenCertification_IsReportedNotHidden(bool isolateProcess)
    {
        _fixtures.WriteRootAnchor();
        _fixtures.WriteRootCrl([]);
        var path = _fixtures.CreateSignedPdf(new SignatureFixtures.SignedPdfRequest
        {
            Certification = true,
            AppendBytesAfterSigning = 64
        });
        using var factory = new SignatureFactory(_fixtures, isolateProcess: isolateProcess);
        using var client = factory.CreateClient();
        var sessionKey = PrepareSession(factory, path);

        using var response = await client.GetAsync($"/signatures/{sessionKey}/0", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"integrity\":\"modified-after-signing\"", body);
        Assert.Contains("\"trust\":\"invalid\"", body);
        Assert.Contains("\"trustReason\":\"modified-after-certification\"", body);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NonPdfSource_ReturnsUnsupportedMediaType(bool isolateProcess)
    {
        var path = _fixtures.CreateSignedPdf(new SignatureFixtures.SignedPdfRequest());
        var tifPath = Path.ChangeExtension(path, ".tif");
        File.Move(path, tifPath);
        using var factory = new SignatureFactory(_fixtures, isolateProcess: isolateProcess);
        using var client = factory.CreateClient();
        var sessionKey = PrepareSession(factory, tifPath, extension: "tif");

        using var response = await client.GetAsync($"/signatures/{sessionKey}/0", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DocumentAboveTheSizeLimit_ReturnsPayloadTooLarge(bool isolateProcess)
    {
        _fixtures.WriteRootAnchor();
        var path = _fixtures.CreateSignedPdf(new SignatureFixtures.SignedPdfRequest());
        using var factory = new SignatureFactory(_fixtures, maxFileBytes: 1024, isolateProcess: isolateProcess);
        using var client = factory.CreateClient();
        var sessionKey = PrepareSession(factory, path);

        using var response = await client.GetAsync($"/signatures/{sessionKey}/0", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Contains("too large", body);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnreadablePdf_ReturnsUnprocessableEntity(bool isolateProcess)
    {
        var path = _fixtures.CreateNonPdf(fileName: "junk.pdf", bytes: 4096);
        using var factory = new SignatureFactory(_fixtures, isolateProcess: isolateProcess);
        using var client = factory.CreateClient();
        var sessionKey = PrepareSession(factory, path);

        using var response = await client.GetAsync($"/signatures/{sessionKey}/0", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("could not be parsed", body);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DocumentWithoutSignatures_ReturnsEmptyList(bool isolateProcess)
    {
        var path = _fixtures.CreateUnsignedPdf();
        using var factory = new SignatureFactory(_fixtures, isolateProcess: isolateProcess);
        using var client = factory.CreateClient();
        var sessionKey = PrepareSession(factory, path);

        using var response = await client.GetAsync($"/signatures/{sessionKey}/0", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"signatures\":[]", body);
    }

    private string PrepareSession(WebApplicationFactory<Program> factory, string filePath, string extension = "pdf")
    {
        var sessions = factory.Services.GetRequiredService<GatewaySessionStore>();
        var stored = sessions.Store(new WebClientPrepRequest
        {
            UserId = "test-user",
            SessionId = "test-session",
            PortableDocuments =
            [
                new WebClientPortableDocument
                {
                    DocumentId = "document-1",
                    FileData = [$"file-1|{extension}|{filePath}"]
                }
            ]
        });
        return stored.Session!.SessionKey;
    }

    public void Dispose() => _fixtures.Dispose();

    private sealed class SignatureFactory : WebApplicationFactory<Program>
    {
        private readonly SignatureFixtures _fixtures;
        private readonly bool _enabled;
        private readonly long? _maxFileBytes;
        private readonly bool _isolateProcess;

        public SignatureFactory(
            SignatureFixtures fixtures,
            bool enabled = true,
            long? maxFileBytes = null,
            bool isolateProcess = true)
        {
            _fixtures = fixtures;
            _enabled = enabled;
            _maxFileBytes = maxFileBytes;
            _isolateProcess = isolateProcess;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ODVGateway:RequireExplicitOpenDocViewerDistPath"] = "true",
                    ["ODVGateway:OpenDocViewerDistPath"] = string.Empty,
                    ["ODVGateway:TrustClientFilePath"] = "true",
                    ["ODVGateway:TrustedSourceRoots:0"] = _fixtures.FileDirectory,
                    ["ODVGateway:signatures:enabled"] = _enabled ? "true" : "false",
                    ["ODVGateway:signatures:isolateProcess"] = _isolateProcess ? "true" : "false",
                    ["ODVGateway:signatures:maxConcurrentValidations"] = "1",
                    ["ODVGateway:signatures:useWindowsTrustedRoots"] = "false",
                    ["ODVGateway:signatures:extraAnchorsDirectory"] = _fixtures.AnchorDirectory,
                    ["ODVGateway:signatures:crlDirectory"] = _fixtures.CrlDirectory,
                    ["ODVGateway:signatures:revocationMode"] = "Online",
                    ["ODVGateway:signatures:revocationTimeoutSeconds"] = "1",
                    ["ODVGateway:signatures:maxFileBytes"] = _maxFileBytes?.ToString()
                }));
        }
    }
}
