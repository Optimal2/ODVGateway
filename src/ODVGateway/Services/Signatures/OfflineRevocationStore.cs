using System.Formats.Asn1;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ODVGateway.Services.Signatures;

/// <summary>
/// Revocation information read from local CRL files. The gateway parses the DER itself because
/// .NET has no managed certificate-revocation-list type: <c>X509Chain</c> either reaches out over
/// HTTP or uses the operating system's cache, and neither is available on an air-gapped server.
/// </summary>
public sealed class OfflineRevocationStore
{
    private static readonly string[] CandidateExtensions = [".crl", ".der", ".pem", ".crt", ".x509", ".r06"];

    private readonly string? directory;
    private readonly ILogger logger;
    private List<ParsedCrl>? cache;

    public OfflineRevocationStore(string? directory, ILogger logger, string contentRootPath)
    {
        this.directory = ResolveDirectory(directory, contentRootPath);
        this.logger = logger;
    }

    /// <summary>True when a CRL directory is configured.</summary>
    public bool IsConfigured => directory is not null;

    /// <summary>
    /// Checks one certificate against the local CRLs of its issuer. The issuer certificate is
    /// needed to verify the CRL signature; without it the list says nothing and is not used.
    /// </summary>
    public CrlVerdict Check(X509Certificate2 certificate, X509Certificate2? issuer, DateTimeOffset at)
    {
        var candidates = Load()
            .Where(crl => crl.IssuerName.SequenceEqual(certificate.IssuerName.RawData))
            .OrderByDescending(crl => crl.ThisUpdate)
            .ToList();
        if (candidates.Count == 0)
        {
            return new CrlVerdict(CrlStatus.NoCrl, null, default, null, null);
        }

        foreach (var crl in candidates)
        {
            string? signatureReason = null;
            var verified = issuer is not null && crl.TryVerify(issuer, out signatureReason);
            if (!verified)
            {
                logger.LogWarning("Signature revocation: CRL signature could not be verified. Reason={Reason}",
                    signatureReason ?? "no issuer certificate is known to verify against");
                continue;
            }

            if (!crl.IsFresh(at))
            {
                return new CrlVerdict(CrlStatus.StaleCrl, null, crl.ThisUpdate, crl.NextUpdate, null);
            }

            if (crl.Entries.TryGetValue(GetSerial(certificate), out var revocationDate))
            {
                return new CrlVerdict(CrlStatus.Revoked, revocationDate, crl.ThisUpdate, crl.NextUpdate, null);
            }

            return new CrlVerdict(CrlStatus.NotListed, null, crl.ThisUpdate, crl.NextUpdate, null);
        }

        return new CrlVerdict(CrlStatus.BadSignature, null, default, null, "no CRL of this issuer verified against its issuer certificate");
    }

    public sealed record CrlVerdict(CrlStatus Status, DateTimeOffset? RevocationDate, DateTimeOffset ThisUpdate,
        DateTimeOffset? NextUpdate, string? Reason);

    public enum CrlStatus
    {
        /// <summary>No local CRL is issued by this certificate's issuer.</summary>
        NoCrl,

        /// <summary>A CRL exists but its window does not cover the validation time.</summary>
        StaleCrl,

        /// <summary>A CRL exists but its signature does not verify for the known issuer.</summary>
        BadSignature,

        /// <summary>Revocation is covered by a verified, fresh CRL and the serial is not listed.</summary>
        NotListed,

        /// <summary>The serial is listed in a verified CRL.</summary>
        Revoked
    }

    private IReadOnlyList<ParsedCrl> Load()
    {
        if (cache is not null)
        {
            return cache;
        }

        cache = new List<ParsedCrl>();
        if (directory is null)
        {
            return cache;
        }

        if (!Directory.Exists(directory))
        {
            logger.LogWarning("Signature revocation: configured CRL directory does not exist. Exists={Exists}", false);
            return cache;
        }

        foreach (var path in EnumerateFiles(directory))
        {
            try
            {
                foreach (var der in ReadDerBlobs(path))
            {
                    var parsed = Parse(der);
                    if (parsed is not null)
                    {
                        cache.Add(parsed);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException)
            {
                // One unreadable file must not remove the other CRLs from the set.
                logger.LogWarning(exception, "Signature revocation: CRL file could not be read. Exception={ExceptionType}",
                    exception.GetType().Name);
            }
        }

        logger.LogInformation("Signature revocation: loaded local CRLs. Count={CrlCount}", cache.Count);
        return cache;
    }

    private static IEnumerable<string> EnumerateFiles(string directory)
    {
        var files = new List<string>();
        foreach (var extension in CandidateExtensions)
        {
            try
            {
                files.AddRange(Directory.EnumerateFiles(directory, "*" + extension));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files;
    }

    private static IEnumerable<byte[]> ReadDerBlobs(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var text = System.Text.Encoding.ASCII.GetString(bytes);
        if (!text.Contains("-----BEGIN", StringComparison.Ordinal))
        {
            yield return bytes;
            yield break;
        }

        foreach (var block in SplitPemBlocks(text))
        {
            yield return block;
        }
    }

    private static IEnumerable<byte[]> SplitPemBlocks(string text)
    {
        var index = 0;
        while (true)
        {
            var start = text.IndexOf("-----BEGIN", index, StringComparison.Ordinal);
            if (start < 0)
            {
                yield break;
            }

            var headerEnd = text.IndexOf("-----", start + 10, StringComparison.Ordinal);
            var end = text.IndexOf("-----END", headerEnd, StringComparison.Ordinal);
            if (headerEnd < 0 || end < 0)
            {
                yield break;
            }

            var body = text[(headerEnd + 5)..end];
            var clean = new string(body.Where(c => !char.IsWhiteSpace(c)).ToArray());
            byte[] decoded;
            try
            {
                decoded = Convert.FromBase64String(clean);
            }
            catch (FormatException)
            {
                index = end + 10;
                continue;
            }

            yield return decoded;
            index = end + 10;
        }
    }

    /// <summary>
    /// CertificateList ::= SEQUENCE { tbsCertList, signatureAlgorithm, signatureValue } (RFC 5280).
    /// Only the fields the gateway needs are read; everything else is skipped by the
    /// length-prefixed reader.
    /// </summary>
    private static ParsedCrl? Parse(byte[] der)
    {
        try
        {
            var outer = new AsnReader(der, AsnEncodingRules.DER).ReadSequence();
            var tbs = outer.ReadEncodedValue().ToArray();
            var signatureAlgorithm = outer.ReadSequence().ReadObjectIdentifier();
            var signatureValue = outer.ReadBitString(out _).ToArray();

            var tbsReader = new AsnReader(tbs, AsnEncodingRules.DER).ReadSequence();
            if (tbsReader.PeekTag() == Asn1Tag.Integer)
            {
                _ = tbsReader.ReadInteger();
            }

            // The TBSCertList repeats the signature algorithm inside the signed region.
            _ = tbsReader.ReadSequence().ReadObjectIdentifier();
            var issuerName = tbsReader.ReadEncodedValue().ToArray();
            var thisUpdate = ReadTime(tbsReader);
            DateTimeOffset? nextUpdate = null;
            if (tbsReader.HasData && IsTimeTag(tbsReader.PeekTag()))
            {
                nextUpdate = ReadTime(tbsReader);
            }

            var entries = new Dictionary<BigInteger, DateTimeOffset>();
            if (tbsReader.HasData && tbsReader.PeekTag() == Asn1Tag.Sequence)
            {
                var revokedCertificates = tbsReader.ReadSequence();
                while (revokedCertificates.HasData)
                {
                    var entry = revokedCertificates.ReadSequence();
                    var serial = entry.ReadInteger();
                    entries[serial] = ReadTime(entry);
                    // Skip per-entry extension EAs if the reader is positioned on them.
                }
            }

            return new ParsedCrl(issuerName, thisUpdate, nextUpdate, entries, signatureAlgorithm, signatureValue, tbs);
        }
        catch (Exception exception) when (exception is AsnContentException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static bool IsTimeTag(Asn1Tag tag) =>
        tag.TagClass == TagClass.Universal &&
        (tag.TagValue == Asn1Tag.UtcTime.TagValue || tag.TagValue == Asn1Tag.GeneralizedTime.TagValue);

    private static DateTimeOffset ReadTime(AsnReader reader) =>
        reader.PeekTag() == Asn1Tag.UtcTime ? reader.ReadUtcTime() : reader.ReadGeneralizedTime();

    /// <summary>
    /// Serial numbers are compared as integers: <see cref="X509Certificate2.SerialNumber"/> is a
    /// big-endian hex string, while the CRL stores a signed INTEGER, so the bytes are reversed and
    /// a leading high bit is neutralised to keep the value positive.
    /// </summary>
    internal static BigInteger GetSerial(X509Certificate2 certificate)
    {
        var hex = certificate.SerialNumber;
        if (string.IsNullOrEmpty(hex))
        {
            return BigInteger.Zero;
        }

        var bigEndian = Convert.FromHexString(hex);
        var littleEndian = new byte[bigEndian.Length + 1];
        for (var i = 0; i < bigEndian.Length; i++)
        {
            littleEndian[bigEndian.Length - 1 - i] = bigEndian[i];
        }

        // littleEndian[^1] stays 0, which keeps a serial whose top bit is set positive.
        return new BigInteger(littleEndian);
    }

    private static string? ResolveDirectory(string? configured, string contentRootPath)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        return Path.IsPathRooted(configured)
            ? configured
            : Path.GetFullPath(Path.Combine(contentRootPath, configured));
    }

    internal sealed record ParsedCrl(
        byte[] IssuerName,
        DateTimeOffset ThisUpdate,
        DateTimeOffset? NextUpdate,
        Dictionary<BigInteger, DateTimeOffset> Entries,
        string SignatureAlgorithm,
        byte[] SignatureValue,
        byte[] SignedRegion)
    {
        public bool IsFresh(DateTimeOffset at) =>
            ThisUpdate <= at.UtcDateTime && (NextUpdate is null || NextUpdate.Value >= at.UtcDateTime);

        /// <summary>
        /// Verifies the CRL signature with the issuer's public key. RSA and ECDSA issuers are
        /// supported; anything else is reported as unverifiable rather than as clean.
        /// </summary>
        public bool TryVerify(X509Certificate2 issuer, out string? reason)
        {
            reason = null;
            var hash = SignatureAlgorithm switch
            {
                "1.2.840.113549.1.1.11" or "1.2.840.113549.1.1.10" or "1.2.840.113549.1.1.14" => HashAlgorithmName.SHA256,
                "1.2.840.113549.1.1.12" => HashAlgorithmName.SHA384,
                "1.2.840.113549.1.1.13" => HashAlgorithmName.SHA512,
                "1.2.840.101.3.4.3.2" => HashAlgorithmName.SHA256,
                "1.2.840.101.3.4.3.3" => HashAlgorithmName.SHA384,
                "1.2.840.101.3.4.3.4" => HashAlgorithmName.SHA512,
                "1.2.840.113549.1.1.5" => HashAlgorithmName.SHA1,
                _ => default
            };

            if (hash.Equals(default(HashAlgorithmName)))
            {
                reason = "unsupported CRL signature algorithm";
                return false;
            }

            var rsa = issuer.GetRSAPublicKey();
            if (rsa is not null)
            {
                var padding = SignatureAlgorithm == "1.2.840.113549.1.1.10"
                    ? RSASignaturePadding.Pss
                    : RSASignaturePadding.Pkcs1;
                if (rsa.VerifyData(SignedRegion, SignatureValue, hash, padding))
                {
                    return true;
                }

                reason = "rsa signature mismatch";
                return false;
            }

            var ecdsa = issuer.GetECDsaPublicKey();
            if (ecdsa is not null)
            {
                if (ecdsa.VerifyData(SignedRegion, SignatureValue, hash))
                {
                    return true;
                }

                reason = "ecdsa signature mismatch";
                return false;
            }

            reason = "issuer has no RSA or ECDSA public key";
            return false;
        }
    }
}
