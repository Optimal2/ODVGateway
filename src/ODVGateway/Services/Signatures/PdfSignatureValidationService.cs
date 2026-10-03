using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using ODVGateway.Models;
using ODVGateway.Options;

namespace ODVGateway.Services.Signatures;

/// <summary>
/// Turns the bytes of one PDF into the signature validation report. Every signature is handled on
/// its own: a signature that cannot be read, parsed or validated produces a verdict instead of an
/// exception, so one broken signature never degrades the answer for the others.
/// </summary>
public sealed class PdfSignatureValidationService
{
    private readonly SignatureValidationOptions options;
    private readonly ILogger logger;
    private readonly PdfSignatureLocator locator = new();
    private readonly PdfSignatureIntegrityVerifier verifier = new();
    private readonly SignatureTrustEvaluator trustEvaluator;

    public PdfSignatureValidationService(
        SignatureValidationOptions options,
        TrustAnchorStore anchors,
        OfflineRevocationStore revocation,
        ILogger logger)
    {
        this.options = options;
        this.logger = logger;
        trustEvaluator = new SignatureTrustEvaluator(options, anchors, revocation, logger);
    }

    public PdfSignatureValidationResponse Validate(byte[] fileBytes) => Validate(fileBytes, CancellationToken.None);

    /// <summary>
    /// Validates every signature dictionary of the document. Throws <see cref="PdfEncryptedException"/>
    /// when the document is encrypted and <see cref="PdfSignatureFormatException"/> when its object
    /// structure cannot be read; the endpoint reports both as 422.
    /// </summary>
    public PdfSignatureValidationResponse Validate(byte[] fileBytes, CancellationToken cancellationToken,
        string? gatewayHost = null)
    {
        var validatedAt = DateTimeOffset.UtcNow;
        if (!HasPdfHeader(fileBytes))
        {
            throw new PdfSignatureFormatException("The file does not start with a readable PDF header.");
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(SignatureValidationLimits.FileBudgetSeconds));
        var token = budget.Token;
        IReadOnlyList<PdfSignatureLocator.SignatureDictionary> signatures;
        try
        {
            signatures = locator.Locate(fileBytes, token);
        }
        catch (PdfEncryptedException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            // PdfPig signals structural damage with a broad set of exception types.
            logger.LogWarning(exception,
                "Signature validation: PDF structure could not be read. Exception={ExceptionType}",
                exception.GetType().Name);
            throw new PdfSignatureFormatException("The PDF object structure could not be read.", exception);
        }

        var results = new List<PdfSignatureValidation>(signatures.Count);
        using var transport = new RevocationHttpClient(options, gatewayHost: gatewayHost);
        foreach (var signature in signatures)
        {
            results.Add(ValidateOne(fileBytes, signature, validatedAt, token, transport));
        }

        long coveredRevisionPrefix = -1;
        for (var i = 0; i < results.Count; i++)
        {
            if (results[i].Integrity == PdfSignatureIntegrity.Intact && results[i].CoversWholeFile == true &&
                signatures[i].ByteRange[0] == 0)
                coveredRevisionPrefix = Math.Max(coveredRevisionPrefix, signatures[i].ByteRange[1]);
        }
        for (var i = 0; i < results.Count; i++)
        {
            var result = results[i];
            if (result.Kind != PdfSignatureKind.Approval || result.Integrity != PdfSignatureIntegrity.Intact ||
                result.CoversWholeFile != false) continue;
            var earlierEnd = signatures[i].ByteRange[2] + signatures[i].ByteRange[3];
            if (coveredRevisionPrefix < earlierEnd)
                results[i] = result with
                {
                    Integrity = PdfSignatureIntegrity.ModifiedAfterSigning,
                    IntegrityReason = SignatureValidationReasons.BytesAppendedAfterSignedRange,
                    Trust = PdfSignatureTrust.Unknown,
                    TrustReason = SignatureValidationReasons.BytesAppendedAfterSignedRange
                };
        }

        logger.LogInformation(
            "Signature validation finished. SignatureCount={SignatureCount} RevocationMode={RevocationMode}",
            results.Count,
            options.RevocationMode);
        return new PdfSignatureValidationResponse(results, validatedAt);
    }

    private PdfSignatureValidation ValidateOne(
        byte[] fileBytes,
        PdfSignatureLocator.SignatureDictionary signature,
        DateTimeOffset validatedAt,
        CancellationToken cancellationToken, RevocationHttpClient transport)
    {
        PdfSignatureIntegrity integrity;
        string? integrityReason;
        bool? coversWholeFile;
        SignedCms? cms;

        try
        {
            var outcome = verifier.Verify(fileBytes, signature, cancellationToken);
            integrity = outcome.Integrity;
            integrityReason = outcome.Reason;
            coversWholeFile = outcome.CoversWholeFile;
            cms = outcome.Cms;
        }
        catch (OperationCanceledException)
        {
            return Project(signature, null, PdfSignatureIntegrity.Unreadable,
                SignatureValidationReasons.ValidationTimeout, null, PdfSignatureTrust.Unknown,
                SignatureValidationReasons.ValidationTimeout, validatedAt, validatedAt, null, null);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            logger.LogWarning(exception,
                "Signature validation: unexpected failure while reading a signature. Exception={ExceptionType}",
                exception.GetType().Name);
            return Project(signature, null, PdfSignatureIntegrity.Unreadable,
                SignatureValidationReasons.ValidationError, null, PdfSignatureTrust.Unknown,
                SignatureValidationReasons.ValidationError, validatedAt, validatedAt, null, null);
        }

        if (cms is not null)
        {
            if (integrity is not (PdfSignatureIntegrity.Intact or PdfSignatureIntegrity.ModifiedAfterSigning))
            {
                // The CMS blob could be read but its own check failed. The signer is still reported —
                // naming who signed an edited document is useful — but a signature whose bytes no longer
                // verify cannot be trusted at any time, so digest-mismatch and signature-invalid are
                // "invalid". Unsupported/unreadable stay "unknown": nothing about the certificate was
                // proven or disproven, the gateway just cannot read that shape.
                var claimed = ReadClaimedSigningTime(signature, cms);
                var (trust, trustReason) = integrity switch
                {
                    PdfSignatureIntegrity.DigestMismatch or PdfSignatureIntegrity.SignatureInvalid =>
                        (PdfSignatureTrust.Invalid, integrityReason ?? SignatureValidationReasons.SignatureInvalid),
                    _ => (PdfSignatureTrust.Unknown, integrityReason ?? SignatureValidationReasons.ValidationError)
                };
                return Project(signature, ReadSignerCertificate(cms), integrity, integrityReason, coversWholeFile,
                    trust, trustReason, validatedAt, validatedAt, claimed.Source, claimed.Value);
            }

            var outcome = EvaluateTrust(signature, cms, integrity, integrityReason, coversWholeFile, validatedAt,
                cancellationToken, transport);
            if (integrity == PdfSignatureIntegrity.ModifiedAfterSigning)
            {
                // A certification signature promises the document will not change afterwards. It did
                // change, so the certificate verdict is forced to "invalid": level 1 must never show a
                // green trust mark for a certification that no longer describes the file.
                return outcome with
                {
                    Trust = PdfSignatureTrust.Invalid,
                    TrustReason = SignatureValidationReasons.ModifiedAfterCertification
                };
            }

            return outcome;
        }

        // Without a readable CMS blob there is no certificate to judge. The trust half of the report
        // stays "unknown" and repeats why the byte-level stage stopped.
        return Project(signature, null, integrity, integrityReason, coversWholeFile, PdfSignatureTrust.Unknown,
            integrityReason ?? SignatureValidationReasons.ValidationError, validatedAt, validatedAt, null, null);
    }

    private PdfSignatureValidation EvaluateTrust(
        PdfSignatureLocator.SignatureDictionary signature,
        SignedCms cms,
        PdfSignatureIntegrity integrity,
        string? integrityReason,
        bool? coversWholeFile,
        DateTimeOffset validatedAt,
        CancellationToken cancellationToken, RevocationHttpClient transport)
    {
        try
        {
            var trust = trustEvaluator.Evaluate(cms, validatedAt, cancellationToken, transport);
            var signingTime = ReadSigningTime(signature, cms, trust);
            return Project(signature, ReadSignerCertificate(cms),
                integrity, integrityReason, coversWholeFile, trust.Trust, trust.Reason, validatedAt,
                trust.ValidationTime, signingTime.Source, signingTime.Value);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Signature validation: time budget exhausted while evaluating trust.");
            return Project(signature, null, integrity, integrityReason, coversWholeFile, PdfSignatureTrust.Unknown,
                SignatureValidationReasons.ValidationTimeout, validatedAt, validatedAt, null, null);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Isolation: an unexpected failure in certificate validation must not fail the response.
            logger.LogWarning(exception,
                "Signature validation: unexpected failure while evaluating trust. Exception={ExceptionType}",
                exception.GetType().Name);
            return Project(signature, null, integrity, integrityReason, coversWholeFile, PdfSignatureTrust.Unknown,
                SignatureValidationReasons.ValidationError, validatedAt, validatedAt, null, null);
        }
    }

    private static PdfSignatureValidation Project(
        PdfSignatureLocator.SignatureDictionary signature,
        X509Certificate2? signer,
        PdfSignatureIntegrity integrity,
        string? integrityReason,
        bool? coversWholeFile,
        PdfSignatureTrust trust,
        string? trustReason,
        DateTimeOffset validatedAt,
        DateTimeOffset validationTime,
        PdfSigningTimeSource? signingTimeSource,
        DateTimeOffset? signingTime) =>
        new()
        {
            FieldName = signature.FieldName,
            Signer = signer is null ? null : X500AttributeValue.Find(signer.SubjectName, X500AttributeValue.CommonNameOid),
            SignerOrganization = signer is null
                ? null
                : X500AttributeValue.Find(signer.SubjectName, X500AttributeValue.OrganizationOid)
                  ?? X500AttributeValue.Find(signer.IssuerName, X500AttributeValue.OrganizationOid),
            Issuer = signer is null ? null : X500AttributeValue.Find(signer.IssuerName, X500AttributeValue.CommonNameOid),
            Serial = signer?.SerialNumber,
            NotBefore = signer is null ? null : ToUtc(signer.NotBefore),
            NotAfter = signer is null ? null : ToUtc(signer.NotAfter),
            SigningTime = signingTime,
            SigningTimeSource = signingTimeSource,
            Reason = signature.Reason,
            Location = signature.Location,
            SubFilter = signature.SubFilter,
            Kind = ReadKind(signature),
            Integrity = integrity,
            IntegrityReason = integrityReason,
            CoversWholeFile = coversWholeFile,
            Trust = trust,
            TrustReason = trustReason,
            ValidationTime = validationTime
        };

    private static PdfSignatureKind ReadKind(PdfSignatureLocator.SignatureDictionary signature)
    {
        if (signature.IsCertification || string.Equals(signature.Transform, "DocMDP", StringComparison.Ordinal))
        {
            return PdfSignatureKind.Certification;
        }

        if (string.Equals(signature.SubFilter, "ETSI.RFC3161", StringComparison.OrdinalIgnoreCase))
        {
            return PdfSignatureKind.Timestamp;
        }

        return PdfSignatureKind.Approval;
    }

    /// <summary>
    /// Signing time, in the order the data contract describes: the CMS <c>signingTime</c> signed
    /// attribute (a claim authenticated by the signature itself), then a verified RFC 3161 token's
    /// generation time, then the signature dictionary's <c>/M</c>. The source is reported next to the
    /// value so the viewer can decide how much to believe.
    /// </summary>
    private static (PdfSigningTimeSource? Source, DateTimeOffset? Value) ReadSigningTime(
        PdfSignatureLocator.SignatureDictionary signature,
        SignedCms cms,
        SignatureTrustEvaluator.Outcome trust)
    {
        var claimed = ReadClaimedSigningTime(signature, cms);
        if (claimed.Source is not null)
        {
            return claimed;
        }

        if (trust.TimestampVerified && trust.TimestampGenerationTime is not null)
        {
            return (PdfSigningTimeSource.Timestamp, trust.TimestampGenerationTime);
        }

        return (PdfSigningTimeSource.None, null);
    }

    /// <summary>
    /// The signing time the document itself claims: the CMS signed attribute, or the signature
    /// dictionary's <c>/M</c> when the attribute is missing.
    /// </summary>
    private static (PdfSigningTimeSource? Source, DateTimeOffset? Value) ReadClaimedSigningTime(
        PdfSignatureLocator.SignatureDictionary signature,
        SignedCms cms)
    {
        var signerInfo = cms.SignerInfos.Count > 0 ? cms.SignerInfos[0] : null;
        if (signerInfo is not null)
        {
            var attribute = PdfSignatureIntegrityVerifier.FindAttribute(signerInfo.SignedAttributes,
                PdfSignatureIntegrityVerifier.SigningTimeOid);
            if (attribute is not null && attribute.Values.Count > 0
                && TryReadTime(attribute.Values[0].RawData, out var cmsTime))
            {
                return (PdfSigningTimeSource.SignedAttribute, cmsTime);
            }
        }

        if (PdfDate.TryParse(signature.ModificationDate, out var pdfTime, out _))
        {
            return (PdfSigningTimeSource.PdfModificationDate, pdfTime);
        }

        return (null, null);
    }

    private static X509Certificate2? ReadSignerCertificate(SignedCms cms) =>
        cms.SignerInfos.Count > 0 ? cms.SignerInfos[0].Certificate : null;

    private static bool TryReadTime(byte[] raw, out DateTimeOffset value)
    {
        // The raw value of a signed attribute is the DER element itself.
        try
        {
            var reader = new AsnReader(raw, AsnEncodingRules.DER);
            value = reader.PeekTag() == Asn1Tag.UtcTime ? reader.ReadUtcTime() : reader.ReadGeneralizedTime();
            return true;
        }
        catch (Exception exception) when (exception is AsnContentException or ArgumentException or InvalidOperationException)
        {
            value = default;
            return false;
        }
    }

    private static DateTimeOffset ToUtc(DateTime value) =>
        value.Kind switch
        {
            DateTimeKind.Local => new DateTimeOffset(value).ToUniversalTime(),
            DateTimeKind.Utc => new DateTimeOffset(value),
            _ => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc))
        };

    private static bool HasPdfHeader(byte[] fileBytes)
    {
        if (fileBytes.Length < SignatureValidationLimits.MinimumPdfBytes)
        {
            return false;
        }

        return fileBytes.Length >= 5
               && fileBytes[0] == (byte)'%'
               && fileBytes[1] == (byte)'P'
               && fileBytes[2] == (byte)'D'
               && fileBytes[3] == (byte)'F';
    }
}

/// <summary>
/// Caps one validation pass so a pathological document cannot pin a request thread indefinitely.
/// Revocation attempts are additionally bounded by <c>Signatures:RevocationTimeoutSeconds</c>.
/// </summary>
public static class SignatureValidationLimits
{
    /// <summary>Wall-clock budget for validating all signatures of one document.</summary>
    public const int FileBudgetSeconds = 60;

    /// <summary>The document must at least look like a PDF to be worth opening.</summary>
    public const int MinimumPdfBytes = 64;
}

/// <summary>
/// Thrown when the PDF object structure cannot be read at all: the endpoint answers 422 instead of
/// pretending that the document carries no signatures.
/// </summary>
public sealed class PdfSignatureFormatException : Exception
{
    public PdfSignatureFormatException(string message) : base(message)
    {
    }

    public PdfSignatureFormatException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when a PDF cannot be validated because it is encrypted: the gateway has no password, and
/// an encrypted document's byte ranges cannot be trusted anyway.
/// </summary>
public sealed class PdfEncryptedException : Exception
{
    public PdfEncryptedException(string message) : base(message)
    {
    }
}
