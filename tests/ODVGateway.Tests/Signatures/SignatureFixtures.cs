using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace ODVGateway.Tests.Signatures;

/// <summary>
/// Builds a throwaway PKI and signed PDFs for the signature validation tests. Everything is created
/// in memory or under a private temp directory: no network, no committed binary fixtures, and no
/// dependency on the machine's certificate stores.
/// </summary>
/// <remarks>
/// The CA hierarchy uses <see cref="CertificateRequest"/> because .NET can only sign with private
/// keys it holds, and the PDFs are assembled by hand: a real signer would use a library, but a test
/// fixture has to control every byte inside the signed range.
/// </remarks>
public sealed class SignatureFixtures : IDisposable
{
    private const string Sha256Oid = "2.16.840.1.101.3.4.2.1";
    private const string SigningCertificateV2Oid = "1.2.840.113549.1.9.16.2.47";
    private const string SignatureTimeStampOid = "1.2.840.113549.1.9.16.2.14";
    private const string TstInfoOid = "1.2.840.113549.1.9.16.1.4";
    private const string DocumentSigningOid = "1.3.6.1.5.5.7.3.4";
    private const string TimeStampingOid = "1.3.6.1.5.5.7.3.8";
    private const int ContentsHexChars = 16384;
    private const int ByteRangeDigits = 14;

    private readonly RSA _rootKey = RSA.Create(2048);
    private readonly RSA _leafKey = RSA.Create(2048);
    private readonly RSA _timestampKey = RSA.Create(2048);
    private readonly RSA _untrustedRootKey = RSA.Create(2048);
    private readonly RSA _untrustedTimestampKey = RSA.Create(2048);
    private readonly List<IDisposable> _extraKeys = [];
    private int _serialCounter;

    public SignatureFixtures()
    {
        TempRoot = System.IO.Directory.CreateTempSubdirectory("odvsig-fixtures-").FullName;
        AnchorDirectory = Path.Combine(TempRoot, "anchors");
        CrlDirectory = Path.Combine(TempRoot, "crl");
        FileDirectory = Path.Combine(TempRoot, "pdf");
        System.IO.Directory.CreateDirectory(AnchorDirectory);
        System.IO.Directory.CreateDirectory(CrlDirectory);
        System.IO.Directory.CreateDirectory(FileDirectory);

        Root = CreateCertificateAuthority("ODVGateway Test Root CA", _rootKey);
        Leaf = IssueLeaf("CN=Test Signer,O=Test Unit AB,C=SE", Now.AddYears(-1), Now.AddYears(1));
        TimestampAuthority = IssueTimestampAuthority("ODVGateway Test TSA", _timestampKey, Now.AddYears(-3), Now.AddYears(1));

        // A second, independent hierarchy. Its responder produces structurally perfect timestamp
        // tokens that chain to an authority the gateway has not configured, which is the case the
        // validator must refuse to believe.
        ForeignRoot = CreateCertificateAuthority("ODVGateway Foreign Root CA", _untrustedRootKey);
        ForeignTimestampAuthority = IssueTimestampAuthority(
            "ODVGateway Foreign TSA", _untrustedTimestampKey, Now.AddYears(-3), Now.AddYears(1), ForeignRoot, _untrustedRootKey);
    }

    /// <summary>Read once so all generated windows are consistent within one fixture.</summary>
    private static DateTimeOffset Now => DateTimeOffset.UtcNow;

    public string TempRoot { get; }

    public string AnchorDirectory { get; }

    public string CrlDirectory { get; }

    public string FileDirectory { get; }

    /// <summary>The throwaway root CA, including its private key (it signs the CRLs).</summary>
    public X509Certificate2 Root { get; }

    /// <summary>A signing certificate that chains to <see cref="Root"/>.</summary>
    public X509Certificate2 Leaf { get; }

    /// <summary>An RFC 3161 responder certificate that chains to <see cref="Root"/>.</summary>
    public X509Certificate2 TimestampAuthority { get; }

    /// <summary>A second root that is deliberately never written to the anchor directory.</summary>
    public X509Certificate2 ForeignRoot { get; }

    /// <summary>A responder under <see cref="ForeignRoot">: valid tokens, untrusted responder.</summary>
    public X509Certificate2 ForeignTimestampAuthority { get; }

    /// <summary>Puts the root in the configured anchor directory. Tests that want an unknown issuer simply do not call this.</summary>
    public void WriteRootAnchor() =>
        File.WriteAllBytes(Path.Combine(AnchorDirectory, "root.cer"), Root.Export(X509ContentType.Cert));

    /// <summary>
    /// Writes a CRL for the root listing the given certificates as revoked. Both validity dates are
    /// explicit, because the interesting cases are a list that was fresh when the signature was made
    /// and a list that has long expired.
    /// </summary>
    public void WriteRootCrl(
        IReadOnlyList<X509Certificate2> revoked,
        DateTimeOffset? thisUpdate = null,
        DateTimeOffset? nextUpdate = null,
        DateTimeOffset? revocationDate = null,
        string fileName = "root.crl")
    {
        var issuedAt = thisUpdate ?? Now.AddMinutes(-5);
        var validUntil = nextUpdate ?? issuedAt.AddDays(7);
        if (validUntil <= issuedAt)
        {
            validUntil = issuedAt.AddDays(1);
        }

        var builder = new CertificateRevocationListBuilder();
        foreach (var certificate in revoked)
        {
            builder.AddEntry(certificate, revocationDate ?? issuedAt, X509RevocationReason.KeyCompromise);
        }

        // CertificateRevocationListBuilder.Build(issuer, number, nextUpdate, hashAlgorithm, padding, thisUpdate)
        var der = builder.Build(
            Root,
            1,
            validUntil,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1,
            issuedAt);
        File.WriteAllBytes(Path.Combine(CrlDirectory, fileName), der);
    }

    /// <summary>
    /// Creates one signed PDF on disk and returns its path. The request controls the certificate,
    /// whether the document is certified rather than approved, the timestamp token, and how the file
    /// is damaged after signing.
    /// </summary>
    public string CreateSignedPdf(SignedPdfRequest request)
    {
        var leaf = request.Certificate ?? Leaf;
        var leafKey = request.CertificateKey ?? _leafKey;
        var signingTime = request.SigningTime ?? Now.AddMinutes(-10);
        var file = BuildSkeleton(request.Certification, !request.WithoutModificationDate, out var contentsStart,
            out var contentsEnd, out var byteRangePosition);

        // The byte range is patched before hashing: it is part of the signed bytes.
        var ranges = new long[] { 0, contentsStart, contentsEnd, file.LongLength - contentsEnd };
        PatchByteRange(file, byteRangePosition, ranges);

        var covered = new byte[ranges[1] + ranges[3]];
        Buffer.BlockCopy(file, 0, covered, 0, (int)ranges[1]);
        Buffer.BlockCopy(file, (int)ranges[2], covered, (int)ranges[1], (int)ranges[3]);

        var cms = new SignedCms(new ContentInfo(covered), detached: true);
        var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, leaf, leafKey)
        {
            IncludeOption = X509IncludeOption.EndCertOnly,
            DigestAlgorithm = new Oid(Sha256Oid)
        };
        signer.Certificates.Add(Root);
        if (request.SigningTimeAttribute)
        {
            signer.SignedAttributes.Add(new Pkcs9SigningTime(signingTime.UtcDateTime));
        }
        else
        {
            // A PAdES-like signature without the claimed signing time: the signing-certificate
            // attribute keeps the signed-attribute set non-empty. An empty set makes .NET drop the
            // required messageDigest, and without a message digest there is nothing to verify the
            // byte ranges against, which is a broken fixture rather than a testable rule.
            signer.SignedAttributes.Add(new Pkcs9AttributeObject(
                new Oid(SigningCertificateV2Oid),
                EssCertIdV2(leaf)));
        }

        cms.ComputeSignature(signer);

        switch (request.TimestampMode)
        {
            case TimestampTokenMode.Valid:
                AttachTimestamp(cms, cms.SignerInfos[0].GetSignature(), request.TimestampTime ?? signingTime);
                break;
            case TimestampTokenMode.Foreign:
                // A structurally valid token over some other signature value: it must not be believed.
                AttachTimestamp(cms, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10], request.TimestampTime ?? signingTime);
                break;
            case TimestampTokenMode.UnanchoredResponder:
                // A token over this very signature, from a responder the gateway has no anchor for:
                // it proves when the signature existed, but not that the statement came from anyone
                // the deployment trusts, so it must not move the validation time.
                AttachTimestamp(cms, cms.SignerInfos[0].GetSignature(), request.TimestampTime ?? signingTime,
                    ForeignTimestampAuthority, _untrustedTimestampKey, ForeignRoot);
                break;
            case TimestampTokenMode.None:
                break;
        }

        WriteContents(file, contentsStart, cms.Encode());

        if (request.Damage == PdfDamage.ChangeSignedByte)
        {
            // Edit the document after signing, inside the first covered range: the CMS blob still
            // verifies against the bytes the signer hashed, but those are no longer the file's bytes.
            var text = Encoding.ASCII.GetString(file);
            var at = text.IndexOf("/Reason (", StringComparison.Ordinal) + "/Reason (".Length;
            file[at] = (byte)'X';
        }

        if (request.AppendBytesAfterSigning > 0)
        {
            // An unsigned increment after the signed range: the classic approval-signature case.
            var withTail = new byte[file.Length + request.AppendBytesAfterSigning];
            Buffer.BlockCopy(file, 0, withTail, 0, file.Length);
            for (var offset = file.Length; offset < withTail.Length; offset++)
            {
                withTail[offset] = (byte)' ';
            }

            file = withTail;
        }

        var path = Path.Combine(FileDirectory, request.FileName ?? $"signed-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, file);
        return path;
    }

    /// <summary>A PDF that parses but carries no signature dictionary.</summary>
    public string CreateUnsignedPdf(string fileName = "unsigned.pdf")
    {
        var body = string.Concat(
            "%PDF-1.7\n",
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n",
            "trailer\n<< /Size 3 /Root 1 0 R >>\nstartxref\n",
            "66\n%%EOF\n");
        var path = Path.Combine(FileDirectory, fileName);
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(body));
        return path;
    }

    /// <summary>Not a PDF at all: used by the size and format guards.</summary>
    public string CreateNonPdf(string fileName = "scan.tif", int bytes = 4096)
    {
        var path = Path.Combine(FileDirectory, fileName);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    /// <summary>Issues an extra leaf (its own key) so a test can use a different validity window.</summary>
    public IssuedCertificate CreateCertificate(string subject, DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        var key = RSA.Create(2048);
        _extraKeys.Add(key);
        return new IssuedCertificate(IssueLeaf(subject, notBefore, notAfter, key), key);
    }

    private void AttachTimestamp(SignedCms cms, byte[] signatureValue, DateTimeOffset generationTime) =>
        AttachTimestamp(cms, signatureValue, generationTime, TimestampAuthority, _timestampKey, Root);

    private void AttachTimestamp(SignedCms cms, byte[] signatureValue, DateTimeOffset generationTime,
        X509Certificate2 responder, RSA responderKey, X509Certificate2 responderIssuer)
    {
        var token = BuildTimestampToken(signatureValue, generationTime, responder, responderKey, responderIssuer);
        cms.SignerInfos[0].AddUnsignedAttribute(
            new Pkcs9AttributeObject(new Oid(SignatureTimeStampOid), token));
    }

    private byte[] BuildTimestampToken(byte[] signatureValue, DateTimeOffset generationTime,
        X509Certificate2 responder, RSA responderKey, X509Certificate2 responderIssuer)
    {
        // The token serial is a plain DER INTEGER; a nullable ReadOnlyMemory parameter cannot take a
        // collection expression, so it is written as an array.
        var tokenSerial = new byte[] { 4, 7, (byte)(_serialCounter % 256) };
        var info = new Rfc3161TimestampTokenInfo(
            new Oid("1.3.6.1.4.1.99999.1"),
            new Oid(Sha256Oid),
            SHA256.HashData(signatureValue),
            tokenSerial,
            generationTime,
            null,
            false,
            null,
            null,
            null!);

        var cms = new SignedCms(new ContentInfo(new Oid(TstInfoOid), info.Encode()));
        var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, responder, responderKey)
        {
            IncludeOption = X509IncludeOption.EndCertOnly,
            DigestAlgorithm = new Oid(Sha256Oid)
        };
        signer.Certificates.Add(responderIssuer);

        // TryDecode only accepts a token whose signer carries a SigningCertificate(V2) signed
        // attribute and whose responder certificate asserts the time-stamping EKU. Both are required
        // of a real responder, so the fixture has to satisfy them to be a realistic token.
        signer.SignedAttributes.Add(new Pkcs9AttributeObject(
            new Oid(SigningCertificateV2Oid),
            EssCertIdV2(responder)));
        cms.ComputeSignature(signer);
        return cms.Encode();
    }

    private static byte[] EssCertIdV2(X509Certificate2 certificate)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        writer.PushSequence(); // SigningCertificateV2 ::= SEQUENCE { certs }
        writer.PushSequence(); // certs ::= SEQUENCE OF ESSCertIDv2
        writer.PushSequence(); // ESSCertIDv2 (omitted hashAlgorithm means sha256)
        writer.WriteOctetString(SHA256.HashData(certificate.RawData));
        writer.PopSequence();
        writer.PopSequence();
        writer.PopSequence();
        return writer.Encode();
    }

    private static void WriteContents(byte[] file, int contentsStart, byte[] der)
    {
        var hex = Convert.ToHexString(der).PadRight(ContentsHexChars, '0');
        for (var i = 0; i < hex.Length; i++)
        {
            file[contentsStart + 1 + i] = (byte)hex[i];
        }
    }

    private static void PatchByteRange(byte[] file, int position, IReadOnlyList<long> ranges)
    {
        foreach (var value in ranges)
        {
            foreach (var digit in value.ToString("D" + ByteRangeDigits))
            {
                file[position++] = (byte)digit;
            }

            position++; // the separator between numbers
        }
    }

    private static byte[] BuildSkeleton(bool certification, bool withModificationDate, out int contentsStart,
        out int contentsEnd, out int byteRangePosition)
    {
        var byteRangePlaceholder = string.Join(" ", Enumerable.Repeat(new string('0', ByteRangeDigits), 4));
        var objects = new List<string>
        {
            string.Empty,
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] /SigFlags 3 >>"
                + (certification ? " /Perms << /DocMDP 5 0 R >>" : string.Empty)
                + " >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] >>",
            "<< /Type /Annot /Subtype /Widget /FT /Sig /T (Signature1) /V 5 0 R /P 3 0 R /Rect [36 36 220 96] >>",
            "<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached"
                + (certification ? " /Transform /DocMDP /TransformParams << /P 1 /Type /RevVerifyInfo >>" : string.Empty)
                + (withModificationDate ? " /M (D:20250101120000Z)" : string.Empty)
                + " /Name (Test Signer) /Reason (Approval) /Location (Testlab)"
                + $" /ByteRange [{byteRangePlaceholder}] /Contents <{new string('0', ContentsHexChars)}> >>"
        };

        var stream = new MemoryStream();
        stream.Write("%PDF-1.7\n%\xE2\xE3\xCF\xD3\n"u8);
        var offsets = new long[objects.Count];
        for (var index = 1; index < objects.Count; index++)
        {
            offsets[index] = stream.Length;
            stream.Write(Encoding.ASCII.GetBytes($"{index} 0 obj\n{objects[index]}\nendobj\n"));
        }

        var xrefOffset = stream.Length;
        var xref = new StringBuilder($"xref\n0 {objects.Count}\n0000000000 65535 f \n");
        for (var index = 1; index < objects.Count; index++)
        {
            xref.Append($"{offsets[index]:D10} 00000 n \n");
        }

        xref.Append($"trailer\n<< /Size {objects.Count} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF\n");
        stream.Write(Encoding.ASCII.GetBytes(xref.ToString()));

        var file = stream.ToArray();
        var text = Encoding.ASCII.GetString(file);
        byteRangePosition = text.IndexOf("/ByteRange [", StringComparison.Ordinal) + "/ByteRange [".Length;
        contentsStart = text.IndexOf("/Contents <", byteRangePosition, StringComparison.Ordinal) + "/Contents ".Length;
        contentsEnd = contentsStart + 1 + ContentsHexChars + 1;
        return file;
    }

    private X509Certificate2 CreateCertificateAuthority(string commonName, RSA key)
    {
        var request = new CertificateRequest($"CN={commonName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request.CreateSelfSigned(Now.AddYears(-2), Now.AddYears(5));
    }

    private X509Certificate2 IssueLeaf(string subject, DateTimeOffset notBefore, DateTimeOffset notAfter) =>
        IssueLeaf(subject, notBefore, notAfter, _leafKey);

    private X509Certificate2 IssueLeaf(string subject, DateTimeOffset notBefore, DateTimeOffset notAfter, RSA key)
    {
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension([new Oid(DocumentSigningOid)], critical: false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        using var issued = request.Create(
            Root.SubjectName,
            X509SignatureGenerator.CreateForRSA(_rootKey, RSASignaturePadding.Pkcs1),
            notBefore.UtcDateTime,
            notAfter.UtcDateTime,
            NextSerial());
        return X509CertificateLoader.LoadCertificate(issued.Export(X509ContentType.Cert));
    }

    private X509Certificate2 IssueTimestampAuthority(string commonName, RSA key, DateTimeOffset notBefore,
        DateTimeOffset notAfter) =>
        IssueTimestampAuthority(commonName, key, notBefore, notAfter, Root, _rootKey);

    private X509Certificate2 IssueTimestampAuthority(string commonName, RSA key, DateTimeOffset notBefore,
        DateTimeOffset notAfter, X509Certificate2 issuer, RSA issuerKey)
    {
        var request = new CertificateRequest($"CN={commonName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));

        // A real responder asserts the time-stamping EKU as critical, and .NET's timestamp decoder
        // rejects a token from a responder certificate that does not.
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension([new Oid(TimeStampingOid)], critical: true));
        using var issued = request.Create(
            issuer.SubjectName,
            X509SignatureGenerator.CreateForRSA(issuerKey, RSASignaturePadding.Pkcs1),
            notBefore.UtcDateTime,
            notAfter.UtcDateTime,
            NextSerial());
        return X509CertificateLoader.LoadCertificate(issued.Export(X509ContentType.Cert));
    }

    private byte[] NextSerial() =>
    [
        (byte)(++_serialCounter >> 8 & 0x7F),
        (byte)(_serialCounter & 0xFF),
        (byte)((_serialCounter >> 3) & 0xFF)
    ];

    public void Dispose()
    {
        _rootKey.Dispose();
        _leafKey.Dispose();
        _timestampKey.Dispose();
        _untrustedRootKey.Dispose();
        _untrustedTimestampKey.Dispose();
        foreach (var key in _extraKeys)
        {
            key.Dispose();
        }

        Root.Dispose();
        Leaf.Dispose();
        TimestampAuthority.Dispose();
        ForeignRoot.Dispose();
        ForeignTimestampAuthority.Dispose();
        try
        {
            if (System.IO.Directory.Exists(TempRoot))
            {
                System.IO.Directory.Delete(TempRoot, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory is not a test failure.
        }
    }

    /// <summary>A freshly issued certificate together with the private key that signs with it.</summary>
    public sealed record IssuedCertificate(X509Certificate2 Certificate, RSA Key);

    public sealed record SignedPdfRequest
    {
        /// <summary>The certificate that signs; defaults to the fixture leaf.</summary>
        public X509Certificate2? Certificate { get; init; }

        public RSA? CertificateKey { get; init; }

        public DateTimeOffset? SigningTime { get; init; }

        /// <summary>Whether the CMS signed attributes carry the signingTime attribute.</summary>
        public bool SigningTimeAttribute { get; init; } = true;

        /// <summary>Omit the signature dictionary's /M entry, so no claimed time is available at all.</summary>
        public bool WithoutModificationDate { get; init; }

        public TimestampTokenMode TimestampMode { get; init; }

        /// <summary>The generation time written into the timestamp token.</summary>
        public DateTimeOffset? TimestampTime { get; init; }

        /// <summary>Certify the document (/Perms/DocMDP) instead of signing it for approval.</summary>
        public bool Certification { get; init; }

        /// <summary>Extra bytes appended after the signed range: an unsigned increment.</summary>
        public int AppendBytesAfterSigning { get; init; }

        public PdfDamage Damage { get; init; }

        public string? FileName { get; init; }
    }

    public enum TimestampTokenMode
    {
        None,

        /// <summary>A token over this signature's value, signed by the fixture responder.</summary>
        Valid,

        /// <summary>A structurally valid token that does not cover this signature.</summary>
        Foreign,

        /// <summary>A token over this signature, signed by a responder with no configured anchor.</summary>
        UnanchoredResponder
    }

    public enum PdfDamage
    {
        None,

        /// <summary>One covered byte is edited after signing: the digest no longer matches.</summary>
        ChangeSignedByte
    }
}
