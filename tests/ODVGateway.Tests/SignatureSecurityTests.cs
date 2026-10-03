using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using ODVGateway.Models;
using ODVGateway.Options;
using ODVGateway.Services.Signatures;
using ODVGateway.Tests.Signatures;

namespace ODVGateway.Tests;

public sealed class SignatureSecurityTests
{
    [Fact]
    public void N4_LargePageTree_WithinSizeLimit_IsAccepted()
    {
        const int pages = 20000;
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Count {pages} /Kids [{string.Join(' ', Enumerable.Range(3, pages).Select(i => $"{i} 0 R"))}] >>"
        };
        objects.AddRange(Enumerable.Repeat("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>", pages));
        var bytes = WriteGraph(objects);
        Assert.True(bytes.Length < 64 * 1024 * 1024);
        Assert.Empty(new PdfSignatureLocator().Locate(bytes, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false, true, 0)]
    [InlineData(true, false, 0)]
    [InlineData(true, true, 0x40000000)]
    [InlineData(true, true, (int)X509ChainStatusFlags.HasNotSupportedCriticalExtension)]
    [InlineData(true, true, (int)X509ChainStatusFlags.InvalidPolicyConstraints)]
    [InlineData(true, true, (int)(X509ChainStatusFlags.Revoked | X509ChainStatusFlags.OfflineRevocation))]
    public void F2_IncompleteOrUnexpectedChain_IsNeverAccepted(bool built, bool anchored, int flags)
    {
        Assert.NotEqual(PdfSignatureTrust.Valid,
            SignatureTrustEvaluator.ClassifyChain(built, anchored, (X509ChainStatusFlags)flags).Trust);
    }

    [Fact]
    public void F2_EveryDefinedStatus_IsRejectedBeforeRevocation()
    {
        foreach (var flag in Enum.GetValues<X509ChainStatusFlags>().Where(f => f != X509ChainStatusFlags.NoError))
            Assert.NotEqual(PdfSignatureTrust.Valid, SignatureTrustEvaluator.ClassifyChain(true, true, flag).Trust);
        Assert.Equal(PdfSignatureTrust.Valid,
            SignatureTrustEvaluator.ClassifyChain(true, true, X509ChainStatusFlags.NoError).Trust);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void F3_RepeatedAndCyclicPageReferences_AreVisitedOnce(bool cycle)
    {
        var bytes = GraphPdf(cycle);
        Assert.Empty(new PdfSignatureLocator().Locate(bytes, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void F4_ParserStreamAndDigest_ObserveCancellationAfterStarting()
    {
        using var cancellation = new CancellationTokenSource();
        using var stream = new CancellablePdfStream(new byte[100], cancellation.Token);
        Assert.Equal(0, stream.ReadByte());
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => stream.ReadByte());
        Assert.ThrowsAny<OperationCanceledException>(() => stream.Seek(0, SeekOrigin.Begin));
        Assert.ThrowsAny<OperationCanceledException>(() =>
            PdfSignatureIntegrityVerifier.TryHash("2.16.840.1.101.3.4.2.1", new byte[100000], out _, cancellation.Token));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("10.1.2.3")]
    [InlineData("100.64.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.2")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    [InlineData("fd12::1")]
    [InlineData("ff02::1")]
    [InlineData("2002:7f00:1::")]
    [InlineData("64:ff9b::7f00:1")]
    [InlineData("2001:db8::1")]
    public void F5_NonPublicAddress_IsBlocked(string value) =>
        Assert.False(RevocationHttpClient.IsPublicAddress(IPAddress.Parse(value)));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("2606:4700:4700::1111")]
    public void PublicAddress_IsAccepted(string value) =>
        Assert.True(RevocationHttpClient.IsPublicAddress(IPAddress.Parse(value)));

    [Fact]
    public async Task F5_Transport_BlocksSchemesPrivateHostsAndRedirects()
    {
        var handler = new Responder(_ => new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri("http://127.0.0.1/secret") }
        });
        using var client = new RevocationHttpClient(new SignatureValidationOptions(), handler, "gateway.example.test");
        foreach (var uri in new[] { "file:///secret", "ldap://example.test/", "http://127.0.0.1/", "http://[::1]/" })
            Assert.Null(await client.FetchAsync(new Uri(uri), TestContext.Current.CancellationToken));
        Assert.Equal(0, handler.Calls);
        Assert.Null(await client.FetchAsync(new Uri("https://example.test/crl"), TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.Calls);
        Assert.False(client.IsAllowedUri(new Uri($"http://{Environment.MachineName}/crl")));
        Assert.False(client.IsAllowedUri(new Uri("https://gateway.example.test/crl")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task F5_Transport_EnforcesSizeFetchAndHostLimits(bool unknownLength)
    {
        var handler = new Responder(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = unknownLength
                ? new StreamContent(new NonSeekableStream(new byte[RevocationHttpClient.MaxResponseBytes + 1]))
                : new ByteArrayContent(new byte[RevocationHttpClient.MaxResponseBytes + 1])
        });
        using var client = new RevocationHttpClient(new SignatureValidationOptions
        { RevocationHostAllowList = ["example.test"] }, handler);
        Assert.False(client.IsAllowedUri(new Uri("https://other.test/crl")));
        Assert.False(client.IsAllowedUri(new Uri("https://example.test.other.test/crl")));
        for (var i = 0; i < 12; i++)
            Assert.Null(await client.FetchAsync(new Uri($"https://example.test/{i}"), TestContext.Current.CancellationToken));
        Assert.Equal(RevocationHttpClient.MaxFetches, handler.Calls);
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    [Fact]
    public void F5_TlsCannotStartUnfilteredNativeDownloads()
    {
        using var client = new RevocationHttpClient(new SignatureValidationOptions());
        using var handler = client.CreateHandler();
        Assert.True(handler.SslOptions.CertificateChainPolicy!.DisableCertificateDownloads);
        Assert.Equal(X509RevocationMode.NoCheck, handler.SslOptions.CertificateChainPolicy.RevocationMode);
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseProxy);
        Assert.NotNull(handler.ConnectCallback);
    }

    [Fact]
    public void F5_MixedDnsAnswerAndOwnAddresses_AreBlocked()
    {
        using var client = new RevocationHttpClient(new SignatureValidationOptions());
        Assert.False(client.AreAllowedAddresses([]));
        Assert.False(client.AreAllowedAddresses([IPAddress.Parse("8.8.8.8"), IPAddress.Loopback]));
        foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            foreach (var address in nic.GetIPProperties().UnicastAddresses)
                Assert.False(client.AreAllowedAddresses([address.Address]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task F5_OnlineCrl_IsVerifiedUsingBoundedInMemoryResponder(bool revoked)
    {
        using var fixtures = new SignatureFixtures();
        fixtures.WriteRootAnchor();
        var leaf = fixtures.CreateCertificate("CN=Online Signer", DateTimeOffset.UtcNow.AddDays(-30),
            DateTimeOffset.UtcNow.AddDays(30), "https://example.test/root.crl");
        fixtures.WriteRootCrl(revoked ? [leaf.Certificate] : []);
        var crl = File.ReadAllBytes(Path.Combine(fixtures.CrlDirectory, "root.crl"));
        var bytes = File.ReadAllBytes(fixtures.CreateSignedPdf(new SignatureFixtures.SignedPdfRequest
        { Certificate = leaf.Certificate, CertificateKey = leaf.Key }));
        var dictionary = Assert.Single(new PdfSignatureLocator().Locate(bytes, TestContext.Current.CancellationToken));
        var integrity = new PdfSignatureIntegrityVerifier().Verify(bytes, dictionary, TestContext.Current.CancellationToken);
        var options = new SignatureValidationOptions
        { UseWindowsTrustedRoots = false, ExtraAnchorsDirectory = fixtures.AnchorDirectory };
        var handler = new Responder(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(crl) });
        using var transport = new RevocationHttpClient(options, handler);
        var evaluator = new SignatureTrustEvaluator(options,
            new TrustAnchorStore(options, NullLogger.Instance, fixtures.TempRoot),
            new OfflineRevocationStore(null, NullLogger.Instance, fixtures.TempRoot), NullLogger.Instance);
        var verdict = await evaluator.EvaluateAsync(integrity.Cms!, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken, transport);
        Assert.Equal(revoked ? PdfSignatureTrust.Invalid : PdfSignatureTrust.Valid, verdict.Trust);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task F5_Transport_TimeoutIsBounded()
    {
        using var client = new RevocationHttpClient(new SignatureValidationOptions { RevocationTimeoutSeconds = 1 },
            new SlowResponder());
        Assert.Null(await client.FetchAsync(new Uri("https://example.test/crl"), TestContext.Current.CancellationToken));
    }

    private sealed class SlowResponder : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), token);
            throw new InvalidOperationException("The transport did not enforce its timeout.");
        }
    }

    internal sealed class Responder(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(respond(request));
        }
    }

    private static byte[] GraphPdf(bool cycle)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 3 0 R] /Count 0 >>"
        };
        for (var i = 3; i <= 16; i++)
        {
            var next = i == 16 ? 3 : i + 1;
            objects.Add(i == 16 && !cycle ? "<< /Type /Pages /Kids [] /Count 0 >>" :
                $"<< /Type /Pages /Kids [{next} 0 R {next} 0 R] /Count 0 >>");
        }
        return WriteGraph(objects);
    }

    private static byte[] WriteGraph(List<string> objects)
    {
        using var stream = new MemoryStream();
        stream.Write("%PDF-1.7\n"u8);
        var offsets = new List<long>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(stream.Position);
            stream.Write(Encoding.ASCII.GetBytes($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"));
        }
        var xref = stream.Position;
        stream.Write(Encoding.ASCII.GetBytes($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n"));
        foreach (var offset in offsets) stream.Write(Encoding.ASCII.GetBytes($"{offset:D10} 00000 n \n"));
        stream.Write(Encoding.ASCII.GetBytes($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n"));
        return stream.ToArray();
    }
}
