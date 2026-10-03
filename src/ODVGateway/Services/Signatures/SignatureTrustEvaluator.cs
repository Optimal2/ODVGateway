using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using ODVGateway.Models;
using ODVGateway.Options;

namespace ODVGateway.Services.Signatures;

/// <summary>Fail-closed chain, validation-time and revocation checks for a CMS signature.</summary>
public sealed class SignatureTrustEvaluator(
    SignatureValidationOptions options, TrustAnchorStore anchors, OfflineRevocationStore revocation, ILogger logger)
{
    public sealed record Outcome(PdfSignatureTrust Trust, string? Reason, DateTimeOffset ValidationTime,
        bool TimestampVerified, DateTimeOffset? TimestampGenerationTime);

    public async Task<Outcome> EvaluateAsync(SignedCms cms, DateTimeOffset now, CancellationToken cancellationToken)
    {
        using var transport = new RevocationHttpClient(options);
        return await EvaluateAsync(cms, now, cancellationToken, transport);
    }

    internal async Task<Outcome> EvaluateAsync(SignedCms cms, DateTimeOffset now, CancellationToken token, RevocationHttpClient transport)
    {
        token.ThrowIfCancellationRequested();
        if (cms.SignerInfos.Count != 1 || cms.SignerInfos[0].Certificate is not { } signer)
            return new(PdfSignatureTrust.Unknown, SignatureValidationReasons.SignerCertificateMissing, now, false, null);
        var signerInfo = cms.SignerInfos[0];
        var (timestamp, timestampFailure) = await ReadTimestampAsync(cms, signerInfo, now, token, transport);
        var at = timestamp ?? now;
        if (IsWeakSignature(signerInfo, signer, out var weakReason))
            return new(PdfSignatureTrust.Invalid, weakReason, at, timestamp.HasValue, timestamp);
        var verdict = await EvaluateCertificateAsync(signer, cms.Certificates, at, token, transport);
        if (verdict.Reason == SignatureValidationReasons.CertificateNotValidAtValidationTime && timestampFailure is not null)
            verdict = (verdict.Trust, timestampFailure);
        token.ThrowIfCancellationRequested();
        return new(verdict.Trust, verdict.Reason, at, timestamp.HasValue, timestamp);
    }

    private async Task<(PdfSignatureTrust Trust, string? Reason)> EvaluateCertificateAsync(X509Certificate2 signer,
        X509Certificate2Collection certificates, DateTimeOffset at, CancellationToken token, RevocationHttpClient transport)
    {
        token.ThrowIfCancellationRequested();
        using var chain = new X509Chain();
        var built = false;
        try
        {
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.VerificationTime = at.UtcDateTime;
            // Never let the native chain engine fetch certificate-controlled revocation URLs.
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.DisableCertificateDownloads = true;
            chain.ChainPolicy.ExtraStore.AddRange(certificates);
            foreach (var anchor in anchors.Anchors) chain.ChainPolicy.CustomTrustStore.Add(anchor);
            built = chain.Build(signer);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Signature validation: chain build failed. Exception={ExceptionType}", ex.GetType().Name);
        }
        token.ThrowIfCancellationRequested();
        var anchored = chain.ChainElements.Count > 0 && anchors.Anchors.Any(anchor =>
            anchor.RawData.AsSpan().SequenceEqual(chain.ChainElements[^1].Certificate.RawData));
        var flags = chain.ChainStatus.Aggregate(X509ChainStatusFlags.NoError, (value, status) => value | status.Status);
        var chainVerdict = ClassifyChain(built, anchored, flags);
        if (chainVerdict.Trust != PdfSignatureTrust.Valid) return chainVerdict;
        foreach (var element in chain.ChainElements)
        {
            if (at.UtcDateTime < element.Certificate.NotBefore.ToUniversalTime() ||
                at.UtcDateTime > element.Certificate.NotAfter.ToUniversalTime())
                return (PdfSignatureTrust.Unknown, SignatureValidationReasons.CertificateNotValidAtValidationTime);
        }
        if (options.RevocationMode == SignatureRevocationMode.NoCheck)
            return (PdfSignatureTrust.Unknown, SignatureValidationReasons.RevocationNotChecked);

        if (chain.ChainElements.Count < 2)
            return (PdfSignatureTrust.Unknown, SignatureValidationReasons.RevocationUnavailable);
        var covered = true;
        for (var i = 0; i < chain.ChainElements.Count - 1; i++)
        {
            token.ThrowIfCancellationRequested();
            var certificate = chain.ChainElements[i].Certificate;
            var issuer = chain.ChainElements[i + 1].Certificate;
            var verdict = revocation.Check(certificate, issuer, at);
            if (verdict.Status is not (OfflineRevocationStore.CrlStatus.NotListed or OfflineRevocationStore.CrlStatus.Revoked)
                && options.RevocationMode == SignatureRevocationMode.Online)
                verdict = await transport.CheckAsync(certificate, issuer, at, token);
            if (verdict.Status == OfflineRevocationStore.CrlStatus.Revoked && verdict.RevocationDate <= at)
                return (PdfSignatureTrust.Invalid, SignatureValidationReasons.Revoked);
            covered &= verdict.Status is OfflineRevocationStore.CrlStatus.NotListed or OfflineRevocationStore.CrlStatus.Revoked;
        }
        token.ThrowIfCancellationRequested();
        return covered ? (PdfSignatureTrust.Valid, null) : (PdfSignatureTrust.Unknown, SignatureValidationReasons.RevocationUnavailable);
    }

    /// <summary>Only a successful, explicitly anchored, status-free chain reaches the revocation gate.</summary>
    internal static (PdfSignatureTrust Trust, string? Reason) ClassifyChain(bool built, bool anchored, X509ChainStatusFlags flags)
    {
        if ((flags & X509ChainStatusFlags.Revoked) != 0)
            return (PdfSignatureTrust.Invalid, SignatureValidationReasons.Revoked);
        if ((flags & (X509ChainStatusFlags.NotSignatureValid | X509ChainStatusFlags.InvalidBasicConstraints |
            X509ChainStatusFlags.InvalidNameConstraints | X509ChainStatusFlags.Cyclic | X509ChainStatusFlags.NotValidForUsage |
            X509ChainStatusFlags.HasWeakSignature | X509ChainStatusFlags.ExplicitDistrust)) != 0)
            return (PdfSignatureTrust.Invalid, SignatureValidationReasons.ChainPolicyViolation);
        if ((flags & (X509ChainStatusFlags.UntrustedRoot | X509ChainStatusFlags.PartialChain)) != 0 || !anchored)
            return (PdfSignatureTrust.Unknown, SignatureValidationReasons.ChainNotAnchored);
        if ((flags & (X509ChainStatusFlags.NotTimeValid | X509ChainStatusFlags.NotTimeNested)) != 0)
            return (PdfSignatureTrust.Unknown, SignatureValidationReasons.CertificateNotValidAtValidationTime);
        if ((flags & (X509ChainStatusFlags.RevocationStatusUnknown | X509ChainStatusFlags.OfflineRevocation)) != 0)
            return (PdfSignatureTrust.Unknown, SignatureValidationReasons.RevocationUnavailable);
        if (!built || flags != X509ChainStatusFlags.NoError)
            return (PdfSignatureTrust.Unknown, SignatureValidationReasons.ValidationError);
        return (PdfSignatureTrust.Valid, null);
    }

    private async Task<(DateTimeOffset? Timestamp, string? Failure)> ReadTimestampAsync(SignedCms cms, SignerInfo signer, DateTimeOffset now,
        CancellationToken token, RevocationHttpClient transport)
    {
        var attribute = PdfSignatureIntegrityVerifier.FindAttribute(signer.UnsignedAttributes,
            PdfSignatureIntegrityVerifier.SignatureTimeStampOid);
        if (attribute is null || attribute.Values.Count == 0) return (null, null);
        token.ThrowIfCancellationRequested();
        if (!Rfc3161TimestampToken.TryDecode(attribute.Values[0].RawData, out var timestamp, out var consumed) ||
            timestamp is null || consumed != attribute.Values[0].RawData.Length ||
            !timestamp.VerifySignatureForSignerInfo(signer, out var responder, null) || responder is null ||
            timestamp.TokenInfo.Timestamp > now)
        {
            return (null, SignatureValidationReasons.TimestampNotVerifiable);
        }
        var timestampCms = timestamp.AsSignedCms();
        if (IsWeakSignature(timestampCms.SignerInfos[0], responder, out _))
        {
            return (null, SignatureValidationReasons.TimestampNotVerifiable);
        }
        var certificates = new X509Certificate2Collection();
        certificates.AddRange(cms.Certificates);
        certificates.AddRange(timestampCms.Certificates);
        var verdict = await EvaluateCertificateAsync(responder, certificates, timestamp.TokenInfo.Timestamp, token, transport);
        if (verdict.Trust != PdfSignatureTrust.Valid)
        {
            var failure = verdict.Reason == SignatureValidationReasons.ChainNotAnchored
                ? SignatureValidationReasons.TimestampResponderNotAnchored : SignatureValidationReasons.TimestampResponderNotTrusted;
            return (null, failure);
        }
        return (timestamp.TokenInfo.Timestamp, null);
    }

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

}
