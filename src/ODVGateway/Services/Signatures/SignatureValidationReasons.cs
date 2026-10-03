namespace ODVGateway.Services.Signatures;

/// <summary>
/// Stable short codes used in <c>integrityReason</c> and <c>trustReason</c>. The codes are part of
/// the contract with the level-1 consumer: they are meant to be switched on, while the free-text
/// signer names stay in the response fields.
/// </summary>
public static class SignatureValidationReasons
{
    public const string ByteRangeMalformed = "byte-range-malformed";
    public const string ByteRangeMissing = "byte-range-missing";
    public const string ContentsMissing = "contents-missing";
    public const string ContentsTooLarge = "contents-too-large";
    public const string SubFilterUnsupported = "subfilter-unsupported";
    public const string CmsUnreadable = "cms-unreadable";
    public const string DigestAlgorithmUnsupported = "digest-algorithm-unsupported";
    public const string MessageDigestAttributeMissing = "message-digest-attribute-missing";
    public const string DigestMismatch = "digest-mismatch";
    public const string SignatureInvalid = "signature-invalid";
    public const string BytesAppendedAfterSignedRange = "bytes-appended-after-signed-range";
    public const string SignerCertificateMissing = "signer-certificate-missing";
    public const string ChainNotAnchored = "chain-not-anchored";
    public const string CertificateNotValidAtValidationTime = "certificate-not-valid-at-validation-time";
    public const string Revoked = "revoked";
    public const string RevocationUnavailable = "revocation-unavailable";
    public const string RevocationNotChecked = "revocation-not-checked";
    public const string WeakSignature = "weak-signature";
    public const string ChainPolicyViolation = "chain-policy-violation";
    public const string ModifiedAfterCertification = "modified-after-certification";
    public const string TimestampNotVerifiable = "timestamp-not-verifiable";
    public const string TimestampResponderNotAnchored = "timestamp-responder-not-anchored";
    public const string TimestampResponderNotTrusted = "timestamp-responder-not-trusted";
    public const string KeyUsageNotSigning = "key-usage-not-signing";
    public const string ValidationError = "validation-error";
    public const string ValidationTimeout = "validation-timeout";
}
