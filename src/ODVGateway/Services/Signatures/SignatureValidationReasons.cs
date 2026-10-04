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

    /// <summary>
    /// Document-level diagnostic: at least one indirect reference named a (number, generation) that
    /// is not in the cross-reference data and was resolved to the newest in-use generation of that
    /// object number instead. Specification-conforming files never need it.
    /// </summary>
    public const string ReferenceGenerationFallback = "reference-generation-fallback";

    /// <summary>A field, widget or annotation reference could not resolve to an existing object.</summary>
    public const string DanglingReferenceSkipped = "dangling-reference-skipped";

    /// <summary>
    /// A cyclic indirect-reference chain was skipped during a field/widget/annotation walk instead
    /// of resolving to a field, /V or signature dictionary.
    /// </summary>
    public const string ReferenceCycleSkipped = "reference-cycle-skipped";

    /// <summary>
    /// An object of an unexpected type was skipped during a field/widget/annotation walk instead
    /// of resolving to the requested field, /V or signature dictionary.
    /// </summary>
    public const string UnexpectedObjectTypeSkipped = "unexpected-object-type-skipped";

    /// <summary>
    /// A reference chain longer than the traversal depth limit was skipped during a
    /// field/widget/annotation walk instead of resolving to the requested field, /V or signature
    /// dictionary.
    /// </summary>
    public const string ReferenceDepthExceeded = "reference-depth-exceeded";

    /// <summary>
    /// Fatal page-tree reason: the pre-open walk of <c>/Root</c> -&gt; <c>/Pages</c> -&gt; <c>/Kids</c>
    /// proved a reference cycle, a self-referencing node or a malformed <c>/Kids</c> entry. Carried in
    /// the <c>PdfSignatureFormatException</c> message; the endpoint answers 422.
    /// </summary>
    public const string PageTreeCyclic = "page-tree-cyclic";

    /// <summary>
    /// Fatal page-tree reason: the page tree exceeds the traversal bound — deeper than 32 levels, more
    /// than 100,000 nodes, or past the shared work budget. Carried in the
    /// <c>PdfSignatureFormatException</c> message; the endpoint answers 422.
    /// </summary>
    public const string PageTreeTooDeep = "page-tree-too-deep";
}
