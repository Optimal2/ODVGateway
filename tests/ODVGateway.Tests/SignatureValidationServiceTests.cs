using Microsoft.Extensions.Logging.Abstractions;
using ODVGateway.Models;
using ODVGateway.Options;
using ODVGateway.Services.Signatures;
using ODVGateway.Tests.Signatures;

namespace ODVGateway.Tests;

/// <summary>
/// Trust and integrity rules of the signature validator, exercised through the real service against
/// throwaway certificates. These tests never touch the network: revocation either comes from a local
/// CRL file written by the fixture, or is deliberately left unresolvable to prove that an unresolved
/// lookup can never produce a "valid" verdict.
/// </summary>
public sealed class SignatureValidationServiceTests : IDisposable
{
    private readonly SignatureFixtures _fixtures = new();

    [Fact]
    public void ValidSignature_CoveredByLocalCrl_IsTrusted()
    {
        _fixtures.WriteRootAnchor();
        _fixtures.WriteRootCrl([]);
        var path = _fixtures.CreateSignedPdf(new SignatureFixtures.SignedPdfRequest());

        var signature = Validate(path, CreateOptions());

        Assert.Equal(PdfSignatureIntegrity.Intact, signature.Integrity);
        Assert.Null(signature.IntegrityReason);
        Assert.Equal(true, signature.CoversWholeFile);
        Assert.Equal(PdfSignatureTrust.Valid, signature.Trust);
        Assert.Null(signature.TrustReason);
        Assert.Equal(PdfSignatureKind.Approval, signature.Kind);
        Assert.Equal(PdfSigningTimeSource.SignedAttribute, signature.SigningTimeSource);
        Assert.NotNull(signature.SigningTime);
        Assert.Equal("Signature1", signature.FieldName);
        Assert.Equal("adbe.pkcs7.detached", signature.SubFilter);
        Assert.Equal("Test Signer", signature.Signer);
        Assert.Equal("Test Unit AB", signature.SignerOrganization);
        Assert.Equal("ODVGateway Test Root CA", signature.Issuer);
        Assert.Equal(_fixtures.Leaf.SerialNumber, signature.Serial);
        Assert.Equal(_fixtures.Leaf.NotBefore.ToUniversalTime(), signature.NotBefore!.Value.ToUniversalTime());
        Assert.Equal("Approval", signature.Reason);
        Assert.Equal("Testlab", signature.Location);
    }

    [Fact]
    public void ExpiredCertificate_WithoutTimestamp_IsUnknownAndNeverValid()
    {
        _fixtures.WriteRootAnchor();
        _fixtures.WriteRootCrl([]);
        var expired = _fixtures.CreateCertificate(
            "CN=Expired Signer,O=Test Unit AB,C=SE",
            NotLongAgo(days: 800),
            NotLongAgo(days: 30));
        var path = _fixtures.CreateSignedPdf(new SignatureFixtures.SignedPdfRequest
        {
            Certificate = expired.Certificate,
            CertificateKey = expired.Key,
            SigningTime = NotLongAgo(days: 400)
        });

        var signature = Validate(path, CreateOptions());

        Assert.Equal(PdfSignatureIntegrity.Intact, signature.Integrity);
        Assert.Equal(PdfSignatureTrust.Unknown, signature.Trust);
        Assert.Equal("certificate-not-valid-at-validation-time", signature.TrustReason);
    }

    [Fact]
    public void ExpiredCertificate_WithVerifiableTimestamp_IsValid()
    {
        // The whole point of a signature timestamp: the certificate expired later, but the signature
        // provably happened while it was live, so the verdict at that instant is valid.
        _fixtures.WriteRootAnchor();
        var signedAt = NotLongAgo(days: 400);
        var expired = _fixtures.CreateCertificate(
            "CN=Expired Signer,O=Test Unit AB,C=SE",
            NotLongAgo(days: 800),
            NotLongAgo(days: 200));
        _fixtures.WriteRootCrl([], thisUpdate: signedAt.AddDays(-30), nextUpdate: signedAt.AddDays(30));
        var path = _fixtures.CreateSignedPdf(new SignatureFixtures.SignedPdfRequest
        {
            Certificate = expired.Certificate,
            CertificateKey = expired.Key,
            SigningTime = signedAt,
            TimestampMode = SignatureFixtures.TimestampTokenMode.Valid,
            TimestampTime = signedAt,
            SigningTimeAttribute = false,
            WithoutModificationDate = true
        });

        var signature = Validate(path, CreateOptions());

        Assert.Equal(PdfSignatureIntegrity.Intact, signature.Integrity);
        Assert.Equal(PdfSignatureTrust.Valid, signature.Trust);
        Assert.Null(signature.TrustReason);
        Assert.Equal(PdfSigningTimeSource.Timestamp, signature.SigningTimeSource);
        Assert.Equal(signedAt.ToUniversalTime().Date, signature.ValidationTime.ToUniversalTime().Date);
    }

    [Fact]
    public void Timestamp_OverAnotherSignature_IsNotBelieved()
    {
        // A well-formed token that does not cover this signature value must not move the validation
        // time: otherwise any captured token from elsewhere would revive an expired certificate.
        _fixtures.WriteRootAnchor();
        var signedAt = NotLongAgo(days: 400);
        var expired = _fixtures.CreateCertificate(
            "CN=Expired Signer,O=Test Unit AB,C=SE",
            NotLongAgo(days: 800),
            NotLongAgo(days: 200));
        _fixtures.WriteRootCrl([], thisUpdate: signedAt.AddDays(-30), nextUpdate: signedAt.AddDays(30));
        var path = _fixtures.CreateSignedPdf(new SignatureFixtures.SignedPdfRequest
        {
            Certificate = expired.Certificate,
            CertificateKey = expired.Key,
            SigningTime = signedAt,
            TimestampMode = SignatureFixtures.TimestampTokenMode.Foreign,
            TimestampTime = signedAt
        });

        var signature = Validate(path, CreateOptions(useLocalCrls: false));

        Assert.Equal(PdfSignatureTrust.Unknown, signature.Trust);
        Assert.Equal("timestamp-not-verifiable", signature.TrustReason);
    }

    [Fact]
    public void TimestampFromUnanchoredResponder_DoesNotReviveAnExpiredCertificate()
    {
        // The token is structurally perfect and covers this very signature, but the responder chains
        // to an authority the gateway has not configured. Believing it would let anyone with a
        // self-signed TSA stamp an expired certificate back into validity.
        _fixtures.WriteRootAnchor();
        var signedAt = NotLongAgo(days: 400);
        var expired = _fixtures.CreateCertificate(
            "CN=Expired Signer,O=Test Unit AB,C=SE",
            NotLongAgo(days: 800),
            NotLongAgo(days: 200));
        _fixtures.WriteRootCrl([], thisUpdate: signedAt.AddDays(-30), nextUpdate: signedAt.AddDays(30));
        var path = _fixtures.CreateSignedPdf(new SignatureFixtures.SignedPdfRequest
        {
            Certificate = expired.Certificate,
            CertificateKey = expired.Key,
            SigningTime = signedAt,
            TimestampMode = SignatureFixtures.TimestampTokenMode.UnanchoredResponder,
            TimestampTime = signedAt
        });

        var signature = Validate(path, CreateOptions());

        Assert.Equal(PdfSignatureIntegrity.Intact, signature.Integrity);
        Assert.Equal(PdfSignatureTrust.Unknown, signature.Trust);
        Assert.Equal("timestamp-responder-not-anchored", signature.TrustReason);
    }

    [Fact]
    public void RevokedCertificate_FromLocalCrl_IsInvalid()
    {
        _fixtures.WriteRootAnchor();
        _fixtures.WriteRootCrl([_fixtures.Leaf]);
        var path = _fixtures.CreateSignedPdf(new SignatureFixtures.SignedPdfRequest());

        var signature = Validate(path, CreateOptions());

        Assert.Equal(PdfSignatureIntegrity.Intact, signature.Integrity);
        Assert.Equal(PdfSignatureTrust.Invalid, signature.Trust);
        Assert.Equal("revoked", signature.TrustReason);
    }

    [Fact]
    public void StaleLocalCrl_IsNotTreatedAsProof()
    {
        // The certificate is revoked, but the only CRL that says so expired years ago. The gateway
        // must refuse to call the signature clean, and must not report it as revoked either.
        _fixtures.WriteRootAnchor();
        _fixtures.WriteRootCrl([_fixtures.Leaf], thisUpdate: NotLongAgo(days: 500), nextUpdate: NotLongAgo(days: 400));
        var path = _fixtures.CreateSignedPdf(new SignatureFixtures.SignedPdfRequest());

        var signature = Validate(path, CreateOptions(revocationMode: SignatureRevocationMode.Offline));

        Assert.NotEqual(PdfSignatureTrust.Valid, signature.Trust);
        Assert.Equal("revocation-unavailable", signature.TrustReason);
    }

    [Fact]
    public void UnknownIssuer_WithoutAnchor_IsUnknown()
    {
        // No anchor written at all: the chain cannot end in a configured trust anchor.
        _fixtures.WriteRootCrl([]);
        var path = _fixtures.CreateSignedPdf(new SignatureFixtures.SignedPdfRequest());

        var signature = Validate(path, CreateOptions());

        Assert.Equal(PdfSignatureIntegrity.Intact, signature.Integrity);
        Assert.Equal(PdfSignatureTrust.Unknown, signature.Trust);
        Assert.Equal("chain-not-anchored", signature.TrustReason);
    }

    [Fact]
    public void EditedDocument_IsReportedAsDigestMismatch()
    {
        _fixtures.WriteRootAnchor();
        _fixtures.WriteRootCrl([]);
        var path = _fixtures.CreateSignedPdf(new SignatureFixtures.SignedPdfRequest
        {
            Damage = SignatureFixtures.PdfDamage.ChangeSignedByte
        });

        var signature = Validate(path, CreateOptions());

        Assert.Equal(PdfSignatureIntegrity.DigestMismatch, signature.Integrity);
        Assert.Equal("digest-mismatch", signature.IntegrityReason);
        Assert.Equal(PdfSignatureTrust.Invalid, signature.Trust);
        Assert.Equal("digest-mismatch", signature.TrustReason);
    }

    [Fact]
    public void F1_AppendedBytes_WithoutLaterIntactSignature_AreNotTrusted()
    {
        // An approval signature only covers the bytes it signed; a later increment is normal. The
        // report has to say that the signed part is intact while the file is no longer fully covered.
        _fixtures.WriteRootAnchor();
        _fixtures.WriteRootCrl([]);
        var path = _fixtures.CreateSignedPdf(new SignatureFixtures.SignedPdfRequest
        {
            AppendBytesAfterSigning = 64
        });

        var signature = Validate(path, CreateOptions());

        Assert.Equal(PdfSignatureIntegrity.ModifiedAfterSigning, signature.Integrity);
        Assert.Equal("bytes-appended-after-signed-range", signature.IntegrityReason);
        Assert.Equal(false, signature.CoversWholeFile);
        Assert.Equal(PdfSignatureTrust.Unknown, signature.Trust);
    }

    [Fact]
    public void AppendedBytes_AfterCertificationSignature_AreAFailure()
    {
        // A /Perms DocMDP certification promises the document will not change. It did, so the
        // signature no longer describes this file and the trust verdict must not be "valid".
        _fixtures.WriteRootAnchor();
        _fixtures.WriteRootCrl([]);
        var path = _fixtures.CreateSignedPdf(new SignatureFixtures.SignedPdfRequest
        {
            Certification = true,
            AppendBytesAfterSigning = 64
        });

        var signature = Validate(path, CreateOptions());

        Assert.Equal(PdfSignatureIntegrity.ModifiedAfterSigning, signature.Integrity);
        Assert.Equal(PdfSignatureTrust.Invalid, signature.Trust);
        Assert.Equal("modified-after-certification", signature.TrustReason);
    }

    [Fact]
    public void Certification_WithoutChanges_IsReportedAsCertification()
    {
        _fixtures.WriteRootAnchor();
        _fixtures.WriteRootCrl([]);
        var path = _fixtures.CreateSignedPdf(new SignatureFixtures.SignedPdfRequest
        {
            Certification = true
        });

        var signature = Validate(path, CreateOptions());

        Assert.Equal(PdfSignatureKind.Certification, signature.Kind);
        Assert.Equal(PdfSignatureIntegrity.Intact, signature.Integrity);
        Assert.Equal(PdfSignatureTrust.Valid, signature.Trust);
    }

    [Fact]
    public void RevocationUnreachable_InOnlineMode_IsNeverValid()
    {
        // No local CRL directory, and the test certificate publishes no revocation addresses: an
        // Online lookup cannot be completed, so the answer must be unknown rather than a silent pass.
        _fixtures.WriteRootAnchor();
        var path = _fixtures.CreateSignedPdf(new SignatureFixtures.SignedPdfRequest());

        var signature = Validate(path, CreateOptions(useLocalCrls: false));

        Assert.NotEqual(PdfSignatureTrust.Valid, signature.Trust);
        Assert.Equal(PdfSignatureTrust.Unknown, signature.Trust);
        Assert.Equal("revocation-unavailable", signature.TrustReason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RevocationSkipped_InNoCheckMode_StillSaysItWasNotChecked(bool localCrl)
    {
        _fixtures.WriteRootAnchor();
        _fixtures.WriteRootCrl([]);
        var path = _fixtures.CreateSignedPdf(new SignatureFixtures.SignedPdfRequest());

        var signature = Validate(path, CreateOptions(
            useLocalCrls: localCrl,
            revocationMode: SignatureRevocationMode.NoCheck));

        Assert.Equal(PdfSignatureTrust.Unknown, signature.Trust);
        Assert.Equal("revocation-not-checked", signature.TrustReason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void F1_UnsignedNoteInsideGap_IsRejected(bool edited)
    {
        _fixtures.WriteRootAnchor();
        _fixtures.WriteRootCrl([]);
        var path = _fixtures.CreateSignedPdf(new SignatureFixtures.SignedPdfRequest
        {
            OversizedGap = true, ChangeUnsignedNote = edited
        });
        var signature = Validate(path, CreateOptions());
        Assert.Equal(PdfSignatureIntegrity.Unreadable, signature.Integrity);
        Assert.NotEqual(PdfSignatureTrust.Valid, signature.Trust);
        Assert.NotEqual(true, signature.CoversWholeFile);
    }

    [Fact]
    public void F7_RevokedTimestampResponder_CannotReviveExpiredSigner()
    {
        _fixtures.WriteRootAnchor();
        var signedAt = NotLongAgo(400);
        var expired = _fixtures.CreateCertificate("CN=Expired Signer", NotLongAgo(800), NotLongAgo(200));
        _fixtures.WriteRootCrl([_fixtures.TimestampAuthority], signedAt.AddDays(-30), signedAt.AddDays(30));
        var path = _fixtures.CreateSignedPdf(new SignatureFixtures.SignedPdfRequest
        {
            Certificate = expired.Certificate, CertificateKey = expired.Key,
            TimestampMode = SignatureFixtures.TimestampTokenMode.Valid, TimestampTime = signedAt
        });
        var signature = Validate(path, CreateOptions());
        Assert.Equal(PdfSignatureTrust.Unknown, signature.Trust);
        Assert.True(signature.ValidationTime > signedAt.AddDays(1));
    }

    [Theory]
    [InlineData(SignatureRevocationMode.Offline)]
    [InlineData(SignatureRevocationMode.NoCheck)]
    public void F7_UncheckedTimestampResponder_DoesNotMoveValidationTime(SignatureRevocationMode mode)
    {
        _fixtures.WriteRootAnchor();
        var signedAt = NotLongAgo(400);
        var expired = _fixtures.CreateCertificate("CN=Expired Signer", NotLongAgo(800), NotLongAgo(200));
        var path = _fixtures.CreateSignedPdf(new SignatureFixtures.SignedPdfRequest
        {
            Certificate = expired.Certificate, CertificateKey = expired.Key,
            TimestampMode = SignatureFixtures.TimestampTokenMode.Valid, TimestampTime = signedAt
        });
        var signature = Validate(path, CreateOptions(useLocalCrls: false, revocationMode: mode));
        Assert.Equal(PdfSignatureTrust.Unknown, signature.Trust);
        Assert.Equal("timestamp-responder-not-trusted", signature.TrustReason);
        Assert.True(signature.ValidationTime > signedAt.AddDays(1));
    }

    [Fact]
    public void F4_CancelledValidation_StopsBeforeParsing()
    {
        var bytes = File.ReadAllBytes(_fixtures.CreateUnsignedPdf());
        Assert.ThrowsAny<OperationCanceledException>(() =>
            CreateService(CreateOptions()).Validate(bytes, new CancellationToken(true)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void F1_OnlyIntactLaterSignature_CoversEarlierRevision(bool damage)
    {
        _fixtures.WriteRootAnchor();
        _fixtures.WriteRootCrl([]);
        var original = File.ReadAllBytes(_fixtures.CreateSignedPdf(new SignatureFixtures.SignedPdfRequest()));
        var bytes = _fixtures.AppendApprovalSignature(original, damage);
        var response = CreateService(CreateOptions()).Validate(bytes, TestContext.Current.CancellationToken);
        Assert.Equal(2, response.Signatures.Count);
        Assert.Equal(damage ? PdfSignatureIntegrity.ModifiedAfterSigning : PdfSignatureIntegrity.Intact,
            response.Signatures[0].Integrity);
        Assert.Equal(damage ? PdfSignatureTrust.Unknown : PdfSignatureTrust.Valid, response.Signatures[0].Trust);
        Assert.Equal(damage ? PdfSignatureIntegrity.DigestMismatch : PdfSignatureIntegrity.Intact,
            response.Signatures[1].Integrity);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void F1_InvalidRangeShapes_AreUnreadable(int variation)
    {
        var bytes = File.ReadAllBytes(_fixtures.CreateSignedPdf(new SignatureFixtures.SignedPdfRequest()));
        var signature = Assert.Single(new PdfSignatureLocator().Locate(bytes, TestContext.Current.CancellationToken));
        var range = signature.ByteRange.ToArray();
        switch (variation)
        {
            case 0: range[0] = 1; break;
            case 1: range[1] = -1; break;
            case 2: range[3] = long.MaxValue; break;
            case 3: range[2] = range[1] - 1; break;
            case 4: range = [.. range, 0, 0]; break;
        }
        var outcome = new PdfSignatureIntegrityVerifier().Verify(bytes, signature with { ByteRange = range },
            TestContext.Current.CancellationToken);
        Assert.Equal(PdfSignatureIntegrity.Unreadable, outcome.Integrity);
    }

    [Fact]
    public void UnsignedDocument_ReportsNoSignatures()
    {
        var path = _fixtures.CreateUnsignedPdf();
        var response = CreateService(CreateOptions())
            .Validate(File.ReadAllBytes(path), TestContext.Current.CancellationToken);

        Assert.Empty(response.Signatures);
        Assert.True(response.ValidatedAt <= DateTimeOffset.UtcNow);
    }

    [Fact]
    public void DocumentWithoutPdfHeader_IsRejectedAsUnreadable()
    {
        var path = _fixtures.CreateNonPdf(fileName: "not-a-pdf.pdf", bytes: 4096);

        Assert.Throws<PdfSignatureFormatException>(() =>
            CreateService(CreateOptions()).Validate(File.ReadAllBytes(path), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void PdfModificationDate_IsTheSigningTimeWhenNothingIsClaimed()
    {
        // No CMS signingTime attribute and no timestamp: the signature dictionary's /M is all the
        // document offers, and the source field has to say that rather than leave it unexplained.
        _fixtures.WriteRootAnchor();
        _fixtures.WriteRootCrl([]);
        var path = _fixtures.CreateSignedPdf(new SignatureFixtures.SignedPdfRequest
        {
            SigningTimeAttribute = false
        });

        var signature = Validate(path, CreateOptions());

        Assert.Equal(PdfSigningTimeSource.PdfModificationDate, signature.SigningTimeSource);
        Assert.Equal(new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero), signature.SigningTime);
    }

    [Fact]
    public void NoSigningTimeEvidence_IsReportedAsNone()
    {
        _fixtures.WriteRootAnchor();
        _fixtures.WriteRootCrl([]);
        var path = _fixtures.CreateSignedPdf(new SignatureFixtures.SignedPdfRequest
        {
            SigningTimeAttribute = false,
            WithoutModificationDate = true
        });

        var signature = Validate(path, CreateOptions());

        Assert.Equal(PdfSigningTimeSource.None, signature.SigningTimeSource);
        Assert.Null(signature.SigningTime);
    }

    private PdfSignatureValidation Validate(string path, SignatureValidationOptions options)
    {
        var response = CreateService(options).Validate(File.ReadAllBytes(path), TestContext.Current.CancellationToken);
        return Assert.Single(response.Signatures);
    }

    /// <summary>
    /// Options for the fixture PKI. <paramref name="crlDirectory"/> is passed through as written:
    /// null means "no local CRLs", which is what makes the Online-unreachable case interesting.
    /// </summary>
    private SignatureValidationOptions CreateOptions(
        bool useLocalCrls = true,
        SignatureRevocationMode revocationMode = SignatureRevocationMode.Online) =>
        new()
        {
            Enabled = true,
            UseWindowsTrustedRoots = false,
            ExtraAnchorsDirectory = _fixtures.AnchorDirectory,
            CrlDirectory = useLocalCrls ? _fixtures.CrlDirectory : null,
            RevocationMode = revocationMode,
            // A short timeout keeps a test that deliberately reaches an unreachable revocation
            // lookup quick, without changing what the rule decides.
            RevocationTimeoutSeconds = 1
        };

    private PdfSignatureValidationService CreateService(SignatureValidationOptions options)
    {
        var logger = NullLogger.Instance;
        var anchors = new TrustAnchorStore(options, logger, _fixtures.TempRoot);
        var revocation = new OfflineRevocationStore(options.CrlDirectory, logger, _fixtures.TempRoot);
        return new PdfSignatureValidationService(options, anchors, revocation, logger);
    }

    private static DateTimeOffset NotLongAgo(int days) => DateTimeOffset.UtcNow.AddDays(-days);

    public void Dispose() => _fixtures.Dispose();
}
