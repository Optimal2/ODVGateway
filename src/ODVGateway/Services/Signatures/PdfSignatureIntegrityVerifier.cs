using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using ODVGateway.Models;

namespace ODVGateway.Services.Signatures;

/// <summary>
/// Byte-level verification of one signature dictionary: the byte ranges, the CMS blob they cover,
/// and the signature value inside it. No trust decision is made here.
/// </summary>
public sealed class PdfSignatureIntegrityVerifier
{
    /// <summary>CMS signed attribute holding the digest of the content (<c>messageDigest</c>).</summary>
    public const string MessageDigestOid = "1.2.840.113549.1.9.4";

    /// <summary>CMS signed attribute holding the signer's claimed signing time.</summary>
    public const string SigningTimeOid = "1.2.840.113549.1.9.5";

    /// <summary>CMS unsigned attribute holding an RFC 3161 signature timestamp token.</summary>
    public const string SignatureTimeStampOid = "1.2.840.113549.1.9.16.2.14";

    private static readonly string[] SupportedSubFilters =
    [
        "adbe.pkcs7.detached",
        "adbe.pkcs7.sha1",
        "ETSI.CAdES.detached",
        "ETSI.RFC3161"
    ];

    /// <summary>
    /// Outcome of the byte-level work: the integrity verdict plus the decoded CMS when it could be
    /// read, so the trust stage does not have to parse it again.
    /// </summary>
    public sealed record IntegrityOutcome(
        PdfSignatureIntegrity Integrity,
        string? Reason,
        bool? CoversWholeFile,
        SignedCms? Cms,
        long UncoveredTailBytes);

    /// <summary>
    /// Verifies <paramref name="signature"/> against <paramref name="fileBytes"/>. Never throws for
    /// a malformed signature: every problem is reported as a verdict.
    /// </summary>
    public IntegrityOutcome Verify(byte[] fileBytes, PdfSignatureLocator.SignatureDictionary signature,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (signature.SubFilter is null ||
            !SupportedSubFilters.Any(known => string.Equals(known, signature.SubFilter, StringComparison.OrdinalIgnoreCase)))
        {
            return Failure(PdfSignatureIntegrity.Unsupported, SignatureValidationReasons.SubFilterUnsupported);
        }

        if (signature.ByteRange.Length == 0)
        {
            return Failure(PdfSignatureIntegrity.Unreadable, SignatureValidationReasons.ByteRangeMissing);
        }

        if (signature.ByteRange.Length != 4)
        {
            return Failure(PdfSignatureIntegrity.Unreadable, SignatureValidationReasons.ByteRangeMalformed);
        }

        if (signature.Contents.Length == 0)
        {
            return Failure(PdfSignatureIntegrity.Unreadable, SignatureValidationReasons.ContentsMissing);
        }

        if (!TryValidateRanges(signature, fileBytes.LongLength, out var coversWholeFile, out var tail))
        {
            return Failure(PdfSignatureIntegrity.Unreadable, SignatureValidationReasons.ByteRangeMalformed, coversWholeFile, tail);
        }

        var cmsBytes = TrimDerPadding(signature.Contents);
        if (cmsBytes.Length == 0)
        {
            return Failure(PdfSignatureIntegrity.Unreadable, SignatureValidationReasons.ContentsMissing, coversWholeFile, tail);
        }

        var covered = ReadCoveredBytes(fileBytes, signature.ByteRange, cancellationToken);
        SignedCms cms;
        try
        {
            // Detached CMS content must be handed to the constructor before Decode: the blob itself
            // carries no content, and SignedCms.ContentInfo is read-only after construction.
            cms = new SignedCms(new ContentInfo(covered), true);
            cms.Decode(cmsBytes);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            return Failure(PdfSignatureIntegrity.Unreadable, SignatureValidationReasons.CmsUnreadable, coversWholeFile, tail);
        }

        if (cms.SignerInfos.Count == 0)
        {
            return Failure(PdfSignatureIntegrity.Unreadable, SignatureValidationReasons.CmsUnreadable, coversWholeFile, tail);
        }

        // The byte-range digest is compared here instead of through CheckHash(): the framework
        // raises the same (localizable) exception text for a digest mismatch and for a signature
        // that does not verify, and those two verdicts mean different things to the reader.
        var digestResult = CompareMessageDigest(cms, covered, cancellationToken);
        if (digestResult != DigestResult.Matches)
        {
            // The CMS blob is still returned for a mismatch: an edited document should not lose the
            // identity of whoever signed it, and the report needs the certificate fields to name them.
            return digestResult switch
            {
                DigestResult.Mismatch => new IntegrityOutcome(PdfSignatureIntegrity.DigestMismatch,
                    SignatureValidationReasons.DigestMismatch, coversWholeFile, cms, tail),
                DigestResult.AttributeMissing => new IntegrityOutcome(PdfSignatureIntegrity.Unsupported,
                    SignatureValidationReasons.MessageDigestAttributeMissing, coversWholeFile, cms, tail),
                _ => new IntegrityOutcome(PdfSignatureIntegrity.Unsupported,
                    SignatureValidationReasons.DigestAlgorithmUnsupported, coversWholeFile, cms, tail)
            };
        }

        try
        {
            cms.CheckSignature(verifySignatureOnly: true);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            return new IntegrityOutcome(PdfSignatureIntegrity.SignatureInvalid,
                SignatureValidationReasons.SignatureInvalid, coversWholeFile, cms, tail);
        }

        var integrity = signature.IsCertification && tail > 0
            ? PdfSignatureIntegrity.ModifiedAfterSigning
            : PdfSignatureIntegrity.Intact;
        return new IntegrityOutcome(
            integrity,
            tail > 0 ? SignatureValidationReasons.BytesAppendedAfterSignedRange : null,
            coversWholeFile,
            cms,
            tail);
    }

    /// <summary>
    /// Exactly two ranges cover the prefix and suffix around this dictionary's physical Contents
    /// hex string. Arithmetic is bounded before addition so malicious lengths cannot overflow.
    /// </summary>
    private static bool TryValidateRanges(PdfSignatureLocator.SignatureDictionary signature, long fileLength,
        out bool coversWholeFile, out long uncoveredTail)
    {
        var byteRange = signature.ByteRange;
        coversWholeFile = false;
        uncoveredTail = 0;
        if (byteRange.Length != 4 || byteRange[0] != 0 || signature.ContentsStart < 0 ||
            signature.ContentsEnd <= signature.ContentsStart || byteRange[1] != signature.ContentsStart ||
            byteRange[2] != signature.ContentsEnd)
            return false;
        long coveredTotal = 0;

        for (var i = 0; i + 1 < byteRange.Length; i += 2)
        {
            var offset = byteRange[i];
            var length = byteRange[i + 1];
            if (offset < 0 || length < 0 || offset > fileLength || length > fileLength - offset)
            {
                return false;
            }

            if (i > 0 && offset < byteRange[i - 2] + byteRange[i - 1])
            {
                return false;
            }

            coveredTotal += length;
        }

        if (coveredTotal == 0 || coveredTotal >= fileLength)
        {
            return false;
        }

        uncoveredTail = fileLength - (byteRange[^2] + byteRange[^1]);
        coversWholeFile = byteRange[0] == 0 && uncoveredTail == 0;
        return true;
    }

    /// <summary>
    /// Concatenates the covered ranges in order, exactly as the signer hashed them.
    /// </summary>
    private static byte[] ReadCoveredBytes(byte[] fileBytes, long[] byteRange, CancellationToken cancellationToken)
    {
        long total = 0;
        for (var i = 0; i + 1 < byteRange.Length; i += 2)
        {
            total += byteRange[i + 1];
        }

        var covered = new byte[total];
        var target = 0;
        for (var i = 0; i + 1 < byteRange.Length; i += 2)
        {
            var length = (int)byteRange[i + 1];
            for (var copied = 0; copied < length;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = Math.Min(65536, length - copied);
                Buffer.BlockCopy(fileBytes, (int)byteRange[i] + copied, covered, target + copied, count);
                copied += count;
            }
            target += length;
        }

        return covered;
    }

    /// <summary>
    /// Signature placeholders are padded with trailing zero bytes after the DER blob. The outer
    /// ContentInfo is self-delimiting, so the real blob is the first encoded value in the buffer.
    /// </summary>
    internal static byte[] TrimDerPadding(byte[] padded)
    {
        if (padded.Length == 0)
        {
            return padded;
        }

        try
        {
            return new AsnReader(padded, AsnEncodingRules.DER).PeekEncodedValue().ToArray();
        }
        catch (AsnContentException)
        {
            return padded;
        }
    }

    private enum DigestResult
    {
        /// <summary>The hashed covered bytes equal the stored messageDigest.</summary>
        Matches,

        /// <summary>They do not: the signed ranges no longer describe this file.</summary>
        Mismatch,

        /// <summary>The signer omitted the messageDigest attribute the byte ranges are bound by.</summary>
        AttributeMissing,

        /// <summary>The CMS names a digest algorithm the gateway cannot compute.</summary>
        AlgorithmUnsupported
    }

    private static DigestResult CompareMessageDigest(SignedCms cms, byte[] covered, CancellationToken cancellationToken)
    {
        var signer = cms.SignerInfos[0];
        if (!TryHash(signer.DigestAlgorithm.Value, covered, out var computed, cancellationToken))
        {
            return DigestResult.AlgorithmUnsupported;
        }

        var attribute = FindAttribute(signer.SignedAttributes, MessageDigestOid);
        if (attribute is null || attribute.Values.Count == 0)
        {
            // No messageDigest at all: the signature never bound these bytes, so there is nothing to
            // compare. That is a shape the gateway does not understand, not a tampering result.
            return DigestResult.AttributeMissing;
        }

        var stored = UnwrapOctetString(attribute.Values[0].RawData);
        return stored.AsSpan().SequenceEqual(computed)
            ? DigestResult.Matches
            : DigestResult.Mismatch;
    }

    /// <summary>
    /// Hashes with the algorithm named by the CMS. MD5 and SHA-1 are still computed: their weakness
    /// is a trust decision, not a reading problem. Unknown or unavailable OIDs report false.
    /// </summary>
    internal static bool TryHash(string? digestOid, byte[] data, out byte[] hash, CancellationToken cancellationToken = default)
    {
        var algorithm = digestOid switch
        {
            "1.2.840.113549.1.1.2" => HashAlgorithmName.MD5,
            "1.3.14.3.2.26" => HashAlgorithmName.SHA1,
            "2.16.840.1.101.3.4.2.1" => HashAlgorithmName.SHA256,
            "2.16.840.1.101.3.4.2.2" => HashAlgorithmName.SHA384,
            "2.16.840.1.101.3.4.2.3" => HashAlgorithmName.SHA512,
            _ => default
        };
        hash = [];
        if (algorithm == default) return false;
        using var hasher = IncrementalHash.CreateHash(algorithm);
        for (var offset = 0; offset < data.Length; offset += 65536)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hasher.AppendData(data, offset, Math.Min(65536, data.Length - offset));
        }
        hash = hasher.GetHashAndReset();
        return true;
    }

    /// <summary>
    /// Digest algorithms that must never end with a trust verdict of "valid".
    /// </summary>
    internal static bool IsWeakDigest(string? digestOid) =>
        digestOid is "1.2.840.113549.1.1.2" or "1.3.14.3.2.26";

    internal static CryptographicAttributeObject? FindAttribute(
        CryptographicAttributeObjectCollection attributes, string oid)
    {
        foreach (var attribute in attributes)
        {
            if (string.Equals(attribute.Oid.Value, oid, StringComparison.Ordinal))
            {
                return attribute;
            }
        }

        return null;
    }

    /// <summary>
    /// A signed attribute's raw value carries the DER element itself; the message digest is stored
    /// as an OCTET STRING and has to be unwrapped before it can be compared.
    /// </summary>
    internal static byte[] UnwrapOctetString(byte[] raw)
    {
        try
        {
            return new AsnReader(raw, AsnEncodingRules.DER).ReadOctetString();
        }
        catch (AsnContentException)
        {
            return raw;
        }
    }

    private static bool IsReadFailure(Exception exception) =>
        exception is CryptographicException or AsnContentException or FormatException or InvalidOperationException;

    private static IntegrityOutcome Failure(PdfSignatureIntegrity integrity, string reason, bool? coversWholeFile = null,
        long tail = 0) =>
        new(integrity, reason, coversWholeFile, null, tail);
}
