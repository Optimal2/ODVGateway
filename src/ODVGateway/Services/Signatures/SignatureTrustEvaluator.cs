using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using ODVGateway.Models;
using ODVGateway.Options;

namespace ODVGateway.Services.Signatures;

/// <summary>
/// Certificate-level verdict for one signature: when to evaluate, whether the chain ends in a
/// configured anchor, and whether revocation was actually checked.
/// </summary>
public sealed class SignatureTrustEvaluator
{
    private readonly SignatureValidationOptions options;
    private readonly TrustAnchorStore anchors;
    private readonly OfflineRevocationStore revocation;
    private readonly ILogger logger;

    public SignatureTrustEvaluator(
        SignatureValidationOptions options,
        TrustAnchorStore anchors,
        OfflineRevocationStore revocation,
        ILogger logger)
    {
        this.options = options;
        this.anchors = anchors;
        this.revocation = revocation;
        this.logger = logger;
    }

    public sealed record Outcome(
        PdfSignatureTrust Trust,
        string? Reason,
        DateTimeOffset ValidationTime,
        bool TimestampVerified,
        DateTimeOffset? TimestampGenerationTime);

    /// <summary>
    /// Builds and classifies the chain for the CMS signer. Never throws: an unexpected failure is
    /// reported as <see cref="PdfSignatureTrust.Unknown"/> with <c>validation-error</c>.
    /// </summary>
    public Outcome Evaluate(SignedCms cms, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var validationTime = now;
        var timestampVerified = false;
        DateTimeOffset? timestampGeneration = null;

        var signerInfo = cms.SignerInfos.Count > 0 ? cms.SignerInfos[0] : null;
        if (signerInfo is null)
        {
            return Unknown(SignatureValidationReasons.CmsUnreadable, validationTime);
        }

        if (TryReadTimestamp(cms, signerInfo, now, validationTime, out var timestampResult, out var timestampFailure))
        {
            validationTime = timestampResult;
            timestampVerified = true;
            timestampGeneration = timestampResult;
        }

        var signer = signerInfo.Certificate;
        if (signer is null)
        {
            return Unknown(SignatureValidationReasons.SignerCertificateMissing, validationTime, timestampVerified,
                timestampGeneration);
        }

        if (IsWeakSignature(signerInfo, signer, out var weakReason))
        {
            return new Outcome(PdfSignatureTrust.Invalid, weakReason, validationTime, timestampVerified, timestampGeneration);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Unknown(SignatureValidationReasons.ValidationTimeout, validationTime);
        }

        var offlineCovered = false;
        var chain = BuildChain(signer, cms.Certificates, validationTime,
            revocation.IsConfigured ? X509RevocationMode.NoCheck : options.GetRevocationMode(), out var offlineOutcome);
        if (revocation.IsConfigured)
        {
            offlineCovered = offlineOutcome == OfflineCoverage.Covered;
            if (offlineOutcome == OfflineCoverage.Revoked)
            {
                return new Outcome(PdfSignatureTrust.Invalid, SignatureValidationReasons.Revoked, validationTime,
                    timestampVerified, timestampGeneration);
            }

            if (!offlineCovered)
            {
                // The local CRLs did not answer for every element: fall back to the configured mode.
                chain = BuildChain(signer, cms.Certificates, validationTime, options.GetRevocationMode(), out _);
            }
        }

        return Classify(chain, validationTime, timestampVerified, timestampGeneration, offlineCovered, timestampFailure);
    }

    private X509Chain BuildChain(
        X509Certificate2 signer,
        X509Certificate2Collection documentCertificates,
        DateTimeOffset validationTime,
        X509RevocationMode revocationMode,
        out OfflineCoverage offlineCoverage)
    {
        offlineCoverage = OfflineCoverage.NotApplicable;
        var chain = new X509Chain();
        try
        {
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.VerificationTime = validationTime.UtcDateTime;
            chain.ChainPolicy.RevocationMode = revocationMode;
            chain.ChainPolicy.RevocationFlag = X509RevocationFlag.EntireChain;
            chain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(Math.Max(1, options.RevocationTimeoutSeconds));

            // Intermediates come from the document only: nothing is downloaded by AIA.
            chain.ChainPolicy.DisableCertificateDownloads = true;
            foreach (var certificate in documentCertificates.OfType<X509Certificate2>())
            {
                chain.ChainPolicy.ExtraStore.Add(certificate);
            }

            foreach (var anchor in anchors.Anchors)
            {
                chain.ChainPolicy.CustomTrustStore.Add(anchor);
            }

            chain.Build(signer);
            offlineCoverage = EvaluateOfflineCoverage(chain, validationTime);
        }
        catch (Exception exception) when (exception is CryptographicException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception,
                "Signature validation: chain building failed. Exception={ExceptionType}", exception.GetType().Name);
        }

        return chain;
    }

    private Outcome Classify(
        X509Chain chain,
        DateTimeOffset validationTime,
        bool timestampVerified,
        DateTimeOffset? timestampGeneration,
        bool offlineCovered,
        string? timestampFailure)
    {
        var flags = chain.ChainStatus.Select(status => status.Status).ToHashSet();

        Outcome Result(PdfSignatureTrust trust, string? reason) =>
            new(trust, reason, validationTime, timestampVerified, timestampGeneration);

        if (flags.Contains(X509ChainStatusFlags.Revoked))
        {
            return Result(PdfSignatureTrust.Invalid, SignatureValidationReasons.Revoked);
        }

        if (flags.Contains(X509ChainStatusFlags.UntrustedRoot) || flags.Contains(X509ChainStatusFlags.PartialChain))
        {
            // Either the chain does not end in a configured anchor, or an intermediate is missing and
            // cannot be downloaded. Neither can be called trusted, and neither is proven bad.
            return Result(PdfSignatureTrust.Unknown, SignatureValidationReasons.ChainNotAnchored);
        }

        if (flags.Contains(X509ChainStatusFlags.NotTimeValid))
        {
            // The chain only reaches its anchors at a time the certificate was not valid for. When the
            // signature did carry a timestamp token that could not be used, say so: it is the more
            // actionable explanation than the bare expiry.
            return Result(PdfSignatureTrust.Unknown,
                timestampFailure ?? SignatureValidationReasons.CertificateNotValidAtValidationTime);
        }

        if (flags.Contains(X509ChainStatusFlags.NotSignatureValid) ||
            flags.Contains(X509ChainStatusFlags.InvalidBasicConstraints) ||
            flags.Contains(X509ChainStatusFlags.InvalidNameConstraints) ||
            flags.Contains(X509ChainStatusFlags.Cyclic) ||
            flags.Contains(X509ChainStatusFlags.NotValidForUsage) ||
            flags.Contains(X509ChainStatusFlags.HasWeakSignature))
        {
            return Result(PdfSignatureTrust.Invalid, SignatureValidationReasons.ChainPolicyViolation);
        }

        var revocationUnresolved = flags.Contains(X509ChainStatusFlags.RevocationStatusUnknown) ||
                                   flags.Contains(X509ChainStatusFlags.OfflineRevocation);
        if (revocationUnresolved && !offlineCovered && options.GetRevocationMode() != X509RevocationMode.NoCheck)
        {
            // Online or Offline mode could not establish the status. The verdict is never "valid" here:
            // an unreachable OCSP responder is not evidence of a live certificate.
            return Result(PdfSignatureTrust.Unknown, SignatureValidationReasons.RevocationUnavailable);
        }

        if (options.GetRevocationMode() == X509RevocationMode.NoCheck && !offlineCovered)
        {
            // "Not checked" is an operator decision, so the verdict may still be valid, but it has to
            // say so: level 1 must never present this as a revocation-clean signature.
            return Result(PdfSignatureTrust.Valid, SignatureValidationReasons.RevocationNotChecked);
        }

        if (IsCleanlyAnchored(chain))
        {
            return Result(PdfSignatureTrust.Valid, timestampFailure);
        }

        return Result(PdfSignatureTrust.Unknown,
            timestampFailure ?? SignatureValidationReasons.ValidationError);
    }

    private Outcome Unknown(string reason, DateTimeOffset validationTime, bool timestampVerified = false,
        DateTimeOffset? timestampGeneration = null) =>
        new(PdfSignatureTrust.Unknown, reason, validationTime, timestampVerified, timestampGeneration);

    /// <summary>
    /// Reads and verifies the RFC 3161 signature timestamp from the CMS unsigned attribute
    /// <c>1.2.840.113549.1.9.16.2.14</c>. A token only moves the validation time into the past when
    /// its own signature covers this signer's signature value <b>and</b> the responder certificate
    /// chains to a configured anchor. A self-signed token from an arbitrary authority would
    /// otherwise be enough to make an expired certificate look live again.
    /// </summary>
    private bool TryReadTimestamp(SignedCms cms, SignerInfo signerInfo, DateTimeOffset now,
        DateTimeOffset currentValidationTime, out DateTimeOffset generationTime, out string? failureReason)
    {
        generationTime = currentValidationTime;
        failureReason = null;

        var attribute = PdfSignatureIntegrityVerifier.FindAttribute(signerInfo.UnsignedAttributes,
            PdfSignatureIntegrityVerifier.SignatureTimeStampOid);
        if (attribute is null || attribute.Values.Count == 0)
        {
            return false;
        }

        if (!Rfc3161TimestampToken.TryDecode(attribute.Values[0].RawData, out var token, out _) || token is null)
        {
            failureReason = SignatureValidationReasons.TimestampNotVerifiable;
            return false;
        }

        if (!token.VerifySignatureForSignerInfo(signerInfo, out var responder, null) || responder is null)
        {
            failureReason = SignatureValidationReasons.TimestampNotVerifiable;
            return false;
        }

        var timestamp = token.TokenInfo.Timestamp;
        if (timestamp > now.AddMinutes(5))
        {
            // A token from the future is not proof of anything.
            failureReason = SignatureValidationReasons.TimestampNotVerifiable;
            return false;
        }

        // The responder is checked for anchoring and validity at its own generation time. Its
        // revocation status is deliberately not looked up here: the signer's chain is evaluated with
        // the configured revocation mode, and doubling every Online lookup would double the outbound
        // traffic for a decision the contract does not report separately.
        if (!IsResponderAnchored(responder, cms.Certificates, timestamp))
        {
            failureReason = SignatureValidationReasons.TimestampResponderNotAnchored;
            return false;
        }

        generationTime = timestamp;
        return true;
    }

    private bool IsResponderAnchored(X509Certificate2 responder, X509Certificate2Collection documentCertificates,
        DateTimeOffset generationTime)
    {
        var chain = BuildChain(responder, documentCertificates, generationTime, X509RevocationMode.NoCheck, out _);
        var anchored = IsCleanlyAnchored(chain);
        chain.Dispose();
        return anchored;
    }

    /// <summary>
    /// True when nothing in the chain says the certificate is untrusted, expired, revoked or broken.
    /// Informational statuses the chain engine may add for a well-formed private CA are ignored.
    /// </summary>
    private static bool IsCleanlyAnchored(X509Chain chain) =>
        !chain.ChainStatus.Any(status => status.Status is X509ChainStatusFlags.UntrustedRoot
            or X509ChainStatusFlags.PartialChain
            or X509ChainStatusFlags.NotTimeValid
            or X509ChainStatusFlags.NotTimeNested
            or X509ChainStatusFlags.Revoked
            or X509ChainStatusFlags.NotSignatureValid
            or X509ChainStatusFlags.HasWeakSignature
            or X509ChainStatusFlags.ExplicitDistrust
            or X509ChainStatusFlags.RevocationStatusUnknown
            or X509ChainStatusFlags.OfflineRevocation);

    /// <summary>
    /// Rejects signatures that cannot be trusted by construction: an MD5 or SHA-1 digest, an RSA key
    /// below 2048 bits, an EC key below 224 bits, or a signing certificate without a signing usage.
    /// </summary>
    private static bool IsWeakSignature(SignerInfo signerInfo, X509Certificate2 signer, out string reason)
    {
        if (PdfSignatureIntegrityVerifier.IsWeakDigest(signerInfo.DigestAlgorithm.Value))
        {
            reason = SignatureValidationReasons.WeakSignature;
            return true;
        }

        using (var rsa = signer.GetRSAPublicKey())
        {
            if (rsa is not null && rsa.KeySize < 2048)
            {
                reason = SignatureValidationReasons.WeakSignature;
                return true;
            }
        }

        using (var ecdsa = signer.GetECDsaPublicKey())
        {
            if (ecdsa is not null && ecdsa.KeySize < 224)
            {
                reason = SignatureValidationReasons.WeakSignature;
                return true;
            }
        }

        var keyUsage = signer.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault();
        if (keyUsage is not null &&
            (keyUsage.KeyUsages & (X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation)) == 0)
        {
            reason = SignatureValidationReasons.KeyUsageNotSigning;
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private enum OfflineCoverage
    {
        /// <summary>No CRL directory configured.</summary>
        NotApplicable,

        /// <summary>Every non-anchor chain element is covered by a verified, fresh CRL.</summary>
        Covered,

        /// <summary>At least one element is listed as revoked.</summary>
        Revoked,

        /// <summary>The local CRLs do not answer for every element.</summary>
        Incomplete
    }

    private OfflineCoverage EvaluateOfflineCoverage(X509Chain chain, DateTimeOffset validationTime)
    {
        if (!revocation.IsConfigured || chain.ChainElements.Count == 0)
        {
            return OfflineCoverage.NotApplicable;
        }

        var covered = 0;
        var expected = 0;
        for (var index = 0; index < chain.ChainElements.Count; index++)
        {
            var certificate = chain.ChainElements[index].Certificate;
            var isAnchor = string.Equals(certificate.Subject, certificate.Issuer, StringComparison.Ordinal)
                           && index == chain.ChainElements.Count - 1;
            if (isAnchor)
            {
                continue;
            }

            expected++;
            var issuer = index + 1 < chain.ChainElements.Count ? chain.ChainElements[index + 1].Certificate : null;
            var verdict = revocation.Check(certificate, issuer, validationTime);
            switch (verdict.Status)
            {
                case OfflineRevocationStore.CrlStatus.Revoked when verdict.RevocationDate <= validationTime:
                    return OfflineCoverage.Revoked;
                case OfflineRevocationStore.CrlStatus.NotListed:
                case OfflineRevocationStore.CrlStatus.Revoked:
                    // Listed only after the validation time: not revoked as of that instant.
                    covered++;
                    break;
                default:
                    continue;
            }
        }

        if (expected == 0)
        {
            return OfflineCoverage.Incomplete;
        }

        return covered == expected ? OfflineCoverage.Covered : OfflineCoverage.Incomplete;
    }
}
