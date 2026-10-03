using System.Formats.Asn1;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging.Abstractions;
using ODVGateway.Models;
using ODVGateway.Options;
using ODVGateway.Services.Signatures;
using ODVGateway.Tests.Signatures;

namespace ODVGateway.Tests;

public sealed class SignatureFollowupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void N1_ArchiveEvidence_IsUsedIncludingRevocation(bool revoked)
    {
        using var f = new SignatureFixtures();
        var at = DateTimeOffset.UtcNow.AddDays(-60);
        f.WriteRootCrl(revoked ? [f.Leaf] : [], at.AddDays(-1), at.AddDays(1), fileName: "archive.crl");
        f.WriteRootCrl([]);
        var store = new OfflineRevocationStore(f.CrlDirectory, NullLogger.Instance, f.TempRoot);
        var result = store.Check(f.Leaf, f.Root, at);
        Assert.Equal(revoked ? OfflineRevocationStore.CrlStatus.Revoked : OfflineRevocationStore.CrlStatus.NotListed,
            result.Status);
        Assert.Equal(OfflineRevocationStore.CrlStatus.StaleCrl, store.Check(f.Leaf, f.Root, at.AddYears(-1)).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task N2_FullScopeIdp_IsAcceptedOnline(bool critical)
    {
        using var f = new SignatureFixtures();
        var leaf = f.CreateCertificate("CN=Online", DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1), "https://example.test/root.crl");
        var crl = Crl(f.Root, Idp(), critical);
        using var transport = new RevocationHttpClient(new SignatureValidationOptions(),
            new SignatureSecurityTests.Responder(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(crl) }));
        var result = await transport.CheckAsync(leaf.Certificate, f.Root, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);
        Assert.Equal(OfflineRevocationStore.CrlStatus.NotListed, result.Status);
    }

    [Theory]
    [InlineData(1, false, true)] // user certificate matches user-only CRL
    [InlineData(1, true, false)]
    [InlineData(2, true, true)] // CA certificate matches CA-only CRL
    [InlineData(2, false, false)]
    [InlineData(3, false, false)] // partial reasons never establish complete coverage
    [InlineData(4, false, false)] // indirect
    [InlineData(5, false, false)] // attribute certificates
    public void N2_ScopeMustCoverCertificate(int flag, bool ca, bool accepted)
    {
        using var f = new SignatureFixtures();
        foreach (var critical in new[] { false, true })
        {
            var result = OfflineRevocationStore.CheckDer(Crl(f.Root, Idp(flag), critical),
                ca ? f.Root : f.Leaf, f.Root, DateTimeOffset.UtcNow);
            Assert.Equal(accepted, result.Status == OfflineRevocationStore.CrlStatus.NotListed);
        }
    }

    [Theory]
    [InlineData("https://example.test/root.crl", true)]
    [InlineData("https://example.test/other.crl", false)]
    public void N2_NamedScope_RequiresMatchingDistributionPoint(string name, bool accepted)
    {
        using var f = new SignatureFixtures();
        var leaf = f.CreateCertificate("CN=Online", DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1), "https://example.test/root.crl");
        var result = OfflineRevocationStore.CheckDer(Crl(f.Root, Idp(name: name), true),
            leaf.Certificate, f.Root, DateTimeOffset.UtcNow);
        Assert.Equal(accepted, result.Status == OfflineRevocationStore.CrlStatus.NotListed);
    }

    [Theory]
    [InlineData("2.5.29.27", false)]
    [InlineData("1.2.3.4", true)]
    public void N2_DeltaAndUnknownCriticalExtensions_RemainRejected(string oid, bool critical)
    {
        using var f = new SignatureFixtures();
        Assert.NotEqual(OfflineRevocationStore.CrlStatus.NotListed,
            OfflineRevocationStore.CheckDer(Crl(f.Root, Idp(), critical, oid), f.Leaf, f.Root, DateTimeOffset.UtcNow).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task N3_Validation_YieldsDuringNetworkWait_AndHonorsCancellation(bool cancel)
    {
        using var f = new SignatureFixtures();
        f.WriteRootAnchor();
        var leaf = f.CreateCertificate("CN=Async", DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1), "https://example.test/root.crl");
        var bytes = File.ReadAllBytes(f.CreateSignedPdf(new SignatureFixtures.SignedPdfRequest
        { Certificate = leaf.Certificate, CertificateKey = leaf.Key }));
        var options = new SignatureValidationOptions { UseWindowsTrustedRoots = false,
            ExtraAnchorsDirectory = f.AnchorDirectory, RevocationTimeoutSeconds = 10 };
        using var handler = new GatedResponder(Crl(f.Root, Idp(), false));
        var service = new PdfSignatureValidationService(options,
            new TrustAnchorStore(options, NullLogger.Instance, f.TempRoot),
            new OfflineRevocationStore(null, NullLogger.Instance, f.TempRoot), NullLogger.Instance, handler);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        // A dedicated thread lets the test detect a sync wait without deadlocking the test runner.
        var invocation = Task.Factory.StartNew(() => service.ValidateAsync(bytes, cancellation.Token),
            cancellation.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            var pending = await invocation.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.False(pending.IsCompleted);
            if (cancel) cancellation.Cancel();
            else handler.Release.TrySetResult();
            var response = await pending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            var signature = Assert.Single(response.Signatures);
            Assert.Equal(cancel ? PdfSignatureTrust.Unknown : PdfSignatureTrust.Valid, signature.Trust);
            if (cancel) Assert.Equal(SignatureValidationReasons.ValidationTimeout, signature.TrustReason);
        }
        finally
        {
            handler.Release.TrySetResult();
            await invocation.Unwrap().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task N2_BaseCrlWithFreshestCrl_CannotProveNotRevoked(bool freshestCrl, bool online)
    {
        using var f = new SignatureFixtures();
        f.WriteRootAnchor();
        var leaf = f.CreateCertificate("CN=Delta CRL coverage", DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1), "https://example.test/root.crl");
        var points = new AsnWriter(AsnEncodingRules.DER);
        points.PushSequence();
        points.WriteEncodedValue(Idp(name: "https://example.test/delta.crl"));
        points.PopSequence();
        // Both signed base CRLs have an empty revokedCertificates list. freshestCRL is non-critical.
        var crl = freshestCrl
            ? Crl(f.Root, points.Encode(), false, "2.5.29.46")
            : Crl(f.Root, Idp(), false);
        if (!online) File.WriteAllBytes(Path.Combine(f.CrlDirectory, "root.crl"), crl);
        var options = new SignatureValidationOptions
        {
            UseWindowsTrustedRoots = false,
            ExtraAnchorsDirectory = f.AnchorDirectory,
            CrlDirectory = online ? null : f.CrlDirectory,
            RevocationMode = online ? SignatureRevocationMode.Online : SignatureRevocationMode.Offline
        };
        using var handler = new SignatureSecurityTests.Responder(_ =>
            new(HttpStatusCode.OK) { Content = new ByteArrayContent(crl) });
        var service = new PdfSignatureValidationService(options,
            new TrustAnchorStore(options, NullLogger.Instance, f.TempRoot),
            new OfflineRevocationStore(options.CrlDirectory, NullLogger.Instance, f.TempRoot),
            NullLogger.Instance, handler);
        var bytes = File.ReadAllBytes(f.CreateSignedPdf(new SignatureFixtures.SignedPdfRequest
        { Certificate = leaf.Certificate, CertificateKey = leaf.Key }));

        var response = await service.ValidateAsync(bytes, TestContext.Current.CancellationToken);

        var signature = Assert.Single(response.Signatures);
        Assert.Equal(freshestCrl ? PdfSignatureTrust.Unknown : PdfSignatureTrust.Valid, signature.Trust);
        Assert.Equal(freshestCrl ? SignatureValidationReasons.RevocationUnavailable : null, signature.TrustReason);
    }

    private sealed class GatedResponder(byte[] crl) : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(token);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(crl) };
        }
    }

    private static byte[] Idp(int? flag = null, string? name = null)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        writer.PushSequence();
        if (name is not null)
        {
            var tag = new Asn1Tag(TagClass.ContextSpecific, 0, true);
            writer.PushSequence(tag);
            writer.PushSequence(tag);
            writer.WriteCharacterString(UniversalTagNumber.IA5String, name, new Asn1Tag(TagClass.ContextSpecific, 6));
            writer.PopSequence(tag);
            writer.PopSequence(tag);
        }
        if (flag is { } value)
        {
            var tag = new Asn1Tag(TagClass.ContextSpecific, value);
            if (value == 3) writer.WriteBitString([0x40], 6, tag);
            else writer.WriteBoolean(true, tag);
        }
        writer.PopSequence();
        return writer.Encode();
    }

    private static byte[] Crl(X509Certificate2 issuer, byte[] extension, bool critical, string oid = "2.5.29.28")
    {
        var algorithm = new AsnWriter(AsnEncodingRules.DER);
        algorithm.PushSequence();
        algorithm.WriteObjectIdentifier("1.2.840.113549.1.1.11");
        algorithm.WriteNull();
        algorithm.PopSequence();
        var tbs = new AsnWriter(AsnEncodingRules.DER);
        tbs.PushSequence();
        tbs.WriteInteger(1);
        tbs.WriteEncodedValue(algorithm.Encode());
        tbs.WriteEncodedValue(issuer.SubjectName.RawData);
        tbs.WriteUtcTime(DateTimeOffset.UtcNow.AddDays(-1));
        tbs.WriteUtcTime(DateTimeOffset.UtcNow.AddDays(1));
        var tag = new Asn1Tag(TagClass.ContextSpecific, 0, true);
        tbs.PushSequence(tag);
        tbs.PushSequence();
        tbs.PushSequence();
        tbs.WriteObjectIdentifier(oid);
        if (critical) tbs.WriteBoolean(true);
        tbs.WriteOctetString(extension);
        tbs.PopSequence();
        tbs.PopSequence();
        tbs.PopSequence(tag);
        tbs.PopSequence();
        using var key = issuer.GetRSAPrivateKey()!;
        var outer = new AsnWriter(AsnEncodingRules.DER);
        outer.PushSequence();
        outer.WriteEncodedValue(tbs.Encode());
        outer.WriteEncodedValue(algorithm.Encode());
        outer.WriteBitString(key.SignData(tbs.Encode(), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        outer.PopSequence();
        return outer.Encode();
    }
}
