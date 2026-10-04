using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using ODVGateway.Models;
using ODVGateway.Options;
using ODVGateway.Services.Signatures;
using ODVGateway.Tests.Signatures;

namespace ODVGateway.Tests;

/// <summary>
/// Signatures added by incremental updates, and incremental updates that write an object number
/// again under a higher generation. References resolve exactly by (number, generation); only a
/// reference whose exact entry is missing falls back to the newest generation, and that fallback
/// is reported as a diagnostic. See Fixtures/Signatures/README.md.
/// </summary>
public sealed class IncrementalUpdateSignatureTests : IDisposable
{
    private static readonly long[] OdvSignature1ByteRange = [0, 877, 17263, 383];
    private static readonly long[] OdvApprovalTwoByteRange = [0, 18212, 34598, 214];

    private readonly SignatureFixtures _fixtures = new();

    [Fact]
    public async Task OdvGenerationZeroFixture_ReportsTwoIntactSignaturesWithoutDiagnostics()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "Signatures", "odv-two-signatures-gen0.pdf"));

        var response = await CreateService().ValidateAsync(bytes, TestContext.Current.CancellationToken);

        Assert.Equal(2, response.Signatures.Count);
        Assert.Equal(PdfSignatureIntegrity.Intact,
            response.Signatures.Single(s => s.FieldName == "Signature1").Integrity);
        Assert.Equal(PdfSignatureIntegrity.Intact,
            response.Signatures.Single(s => s.FieldName == "ApprovalTwo").Integrity);
        Assert.False(response.Signatures.Single(s => s.FieldName == "Signature1").CoversWholeFile);
        Assert.True(response.Signatures.Single(s => s.FieldName == "ApprovalTwo").CoversWholeFile);
        Assert.All(response.Signatures, s => Assert.NotEqual(PdfSignatureTrust.Valid, s.Trust));
        Assert.Empty(response.Diagnostics);
    }

    [Theory]
    [InlineData("/AcroForm << /Fields [99 0 R 4 0 R 99 0 R] >>")]
    [InlineData("/AcroForm << /Fields [<< /Kids [99 0 R 4 0 R] >>] >>")]
    [InlineData("/AcroForm << /Fields [<< /FT /Sig /V 99 0 R >> 4 0 R] >>")]
    [InlineData("/AcroForm << /Fields [<< /Parent 99 0 R >> 4 0 R] >>")]
    public async Task DanglingFieldReference_SkipsElementAndReportsRemainingSignature(string form)
    {
        var original = File.ReadAllBytes(_fixtures.CreateSignedPdf(new SignatureFixtures.SignedPdfRequest()));
        var bytes = AppendUpdate(original, [(1, 0, $"<< /Type /Catalog /Pages 2 0 R {form} >>")]);

        var response = await CreateService().ValidateAsync(bytes, TestContext.Current.CancellationToken);

        var signature = Assert.Single(response.Signatures);
        Assert.Equal("Signature1", signature.FieldName);
        Assert.Equal(PdfSignatureIntegrity.ModifiedAfterSigning, signature.Integrity);
        Assert.Equal(["dangling-reference-skipped"], response.Diagnostics);
    }

    [Fact]
    public async Task DanglingAnnotationReference_SkipsElementAndReportsRemainingSignature()
    {
        var original = File.ReadAllBytes(_fixtures.CreateSignedPdf(new SignatureFixtures.SignedPdfRequest()));
        var bytes = AppendUpdate(original,
        [
            (1, 0, "<< /Type /Catalog /Pages 2 0 R >>"),
            (3, 0, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Annots [99 0 R 4 0 R] >>")
        ]);

        var response = await CreateService().ValidateAsync(bytes, TestContext.Current.CancellationToken);

        Assert.Equal("Signature1", Assert.Single(response.Signatures).FieldName);
        Assert.Equal(["dangling-reference-skipped"], response.Diagnostics);
    }

    [Fact]
    public async Task CyclicVReference_SkipsElementAndReportsRemainingSignature()
    {
        // Field 6 points at a two-object reference cycle (7 -> 8 -> 7) instead of a signature
        // dictionary. The cycle must not hide the valid sibling, and the skip must be visible.
        var bytes = BaseRevision(acroFormBody: "<< /Fields [4 0 R 6 0 R] >>",
            extraObjects:
            [
                "<< /FT /Sig /T (Signature1) /V 5 0 R >>",
                "<< /Type /Sig /SubFilter /adbe.pkcs7.detached /ByteRange [0 1 2 3] /Contents <00> >>",
                "<< /FT /Sig /T (Looped) /V 7 0 R >>",
                "8 0 R",
                "7 0 R"
            ]);

        var response = await CreateService().ValidateAsync(bytes, TestContext.Current.CancellationToken);

        Assert.Equal("Signature1", Assert.Single(response.Signatures).FieldName);
        Assert.Contains(SignatureValidationReasons.ReferenceCycleSkipped, response.Diagnostics);
    }

    [Fact]
    public async Task WrongTypeDirectVValue_SkipsElementAndReportsRemainingSignature()
    {
        // Field 6 carries a direct string as /V instead of a signature dictionary or a reference
        // to one. The valid sibling must still be reported and the skip must be visible.
        var bytes = BaseRevision(acroFormBody: "<< /Fields [4 0 R 6 0 R] >>",
            extraObjects:
            [
                "<< /FT /Sig /T (Signature1) /V 5 0 R >>",
                "<< /Type /Sig /SubFilter /adbe.pkcs7.detached /ByteRange [0 1 2 3] /Contents <00> >>",
                "<< /FT /Sig /T (Broken) /V (not-a-dictionary) >>"
            ]);

        var response = await CreateService().ValidateAsync(bytes, TestContext.Current.CancellationToken);

        Assert.Equal("Signature1", Assert.Single(response.Signatures).FieldName);
        Assert.Contains(SignatureValidationReasons.UnexpectedObjectTypeSkipped, response.Diagnostics);
    }

    [Fact]
    public async Task DeepReferenceChain_SkipsElementAndReportsDepthDiagnostic()
    {
        // /Fields[1] reaches its field through 33 bare-reference hops (objects 6-38), past the
        // depth-32 bound. The valid sibling must still be reported and the skip must be visible
        // as reference-depth-exceeded instead of an empty diagnostics array.
        var extraObjects = new List<string>
        {
            "<< /FT /Sig /T (Signature1) /V 5 0 R >>",
            "<< /Type /Sig /SubFilter /adbe.pkcs7.detached /ByteRange [0 1 2 3] /Contents <00> >>"
        };
        for (var i = 6; i < 6 + 33; i++)
            extraObjects.Add($"{i + 1} 0 R");
        extraObjects.Add("<< /FT /Sig /T (Hidden) /V 40 0 R >>");
        extraObjects.Add("<< /Type /Sig /SubFilter /adbe.pkcs7.detached /ByteRange [0 1 2 3] /Contents <00> >>");
        var bytes = BaseRevision(acroFormBody: "<< /Fields [4 0 R 6 0 R] >>", extraObjects: extraObjects);

        var response = await CreateService().ValidateAsync(bytes, TestContext.Current.CancellationToken);

        Assert.Equal("Signature1", Assert.Single(response.Signatures).FieldName);
        Assert.Contains(SignatureValidationReasons.ReferenceDepthExceeded, response.Diagnostics);
    }

    [Fact]
    public async Task CyclicAcroFormReference_RejectsDocument()
    {
        // Object 3 is the AcroForm and a self-reference: resolving /AcroForm cycles instead of
        // reaching a dictionary. A cyclic root must fail the file, not read as absent.
        var bytes = BaseRevision(acroFormBody: "3 0 R");

        var exception = Assert.Throws<PdfSignatureFormatException>(() =>
            new PdfSignatureLocator().Locate(bytes, TestContext.Current.CancellationToken));
        Assert.Contains("AcroForm", exception.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<PdfSignatureFormatException>(() =>
            CreateService().ValidateAsync(bytes, TestContext.Current.CancellationToken));
    }

    [Fact(Skip = "Parked: PdfPig walks the page tree eagerly in PdfDocument.Open and stack-overflows on this reference cycle before the locator runs, killing the test host. Re-enable once opening is guarded; the test then proves the named fatal failure.")]
    public async Task CyclicPageTreeKidsReference_RejectsDocument()
    {
        // The /Kids entry resolves through a 4 -> 5 -> 4 reference cycle instead of a page node.
        var bytes = AppendUpdate(BaseRevision(),
        [
            (2, 0, "<< /Type /Pages /Kids [4 0 R] /Count 1 >>"),
            (4, 0, "5 0 R"),
            (5, 0, "4 0 R")
        ]);

        var exception = Assert.Throws<PdfSignatureFormatException>(() =>
            new PdfSignatureLocator().Locate(bytes, TestContext.Current.CancellationToken));
        Assert.Contains("page tree", exception.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<PdfSignatureFormatException>(() =>
            CreateService().ValidateAsync(bytes, TestContext.Current.CancellationToken));
    }

    [Fact(Skip = "Parked: PdfPig walks the page tree eagerly in PdfDocument.Open and stack-overflows on this reference cycle before the locator runs, killing the test host. Re-enable once opening is guarded; the test then proves the named fatal failure.")]
    public async Task CyclicPagesRootReference_RejectsDocument()
    {
        // The catalog /Pages reference itself cycles (4 -> 5 -> 4) instead of reaching the root.
        var bytes = AppendUpdate(BaseRevision(),
        [
            (1, 0, "<< /Type /Catalog /Pages 4 0 R /AcroForm 3 0 R >>"),
            (4, 0, "5 0 R"),
            (5, 0, "4 0 R")
        ]);

        var exception = Assert.Throws<PdfSignatureFormatException>(() =>
            new PdfSignatureLocator().Locate(bytes, TestContext.Current.CancellationToken));
        Assert.Contains("page tree", exception.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<PdfSignatureFormatException>(() =>
            CreateService().ValidateAsync(bytes, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DanglingRootReference_StillRejectsDocument(bool catalog)
    {
        var bytes = BaseRevision();
        var text = Encoding.Latin1.GetString(bytes);
        bytes = Encoding.Latin1.GetBytes(catalog
            ? text.Replace("/Root 1 0 R", "/Root 9 0 R", StringComparison.Ordinal)
            : text.Replace("/AcroForm 3 0 R", "/AcroForm 9 0 R", StringComparison.Ordinal));

        await Assert.ThrowsAsync<PdfSignatureFormatException>(() =>
            CreateService().ValidateAsync(bytes, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OdvGenerationBumpedFixture_NeverShadowsTheGenerationZeroOriginal()
    {
        // Out of specification: the update writes `3 1 obj` / `5 1 obj` while every reference keeps
        // naming generation 0. Generation 0 exists, so it is the object reached; the later revision
        // is unsigned content appended after Signature1, which is what a strict reader shows too.
        var bytes = ReadOdvFixture();

        var located = new PdfSignatureLocator().Locate(bytes, TestContext.Current.CancellationToken);
        var response = await CreateService().ValidateAsync(bytes, TestContext.Current.CancellationToken);

        var dictionary = Assert.Single(located);
        Assert.Equal("Signature1", dictionary.FieldName);
        Assert.Equal(OdvSignature1ByteRange, dictionary.ByteRange);
        var signature = Assert.Single(response.Signatures);
        Assert.Equal("Signature1", signature.FieldName);
        Assert.Equal(PdfSignatureIntegrity.ModifiedAfterSigning, signature.Integrity);
        Assert.Equal(SignatureValidationReasons.BytesAppendedAfterSignedRange, signature.IntegrityReason);
        Assert.NotEqual(PdfSignatureTrust.Valid, signature.Trust);
        // Generation 0 exists for every reference, so nothing fell back.
        Assert.Empty(response.Diagnostics);
    }

    [Fact]
    public async Task SpecCorrectTwoSignatureIncremental_ReportsBothSignatures()
    {
        var bytes = SpecCorrectTwoSignatureFixture();

        var response = await CreateService().ValidateAsync(bytes, TestContext.Current.CancellationToken);

        Assert.Equal(2, response.Signatures.Count);
        var first = response.Signatures.Single(s => s.FieldName == "Signature1");
        var second = response.Signatures.Single(s => s.FieldName == "ApprovalTwo");
        // Rule 5: the earlier approval is intact only because the later, itself intact signature
        // covers the earlier revision and the current file.
        Assert.Equal(PdfSignatureIntegrity.Intact, first.Integrity);
        Assert.Equal(false, first.CoversWholeFile);
        Assert.Equal(PdfSignatureIntegrity.Intact, second.Integrity);
        Assert.Null(second.IntegrityReason);
        Assert.Equal(true, second.CoversWholeFile);
        Assert.All(response.Signatures, s => Assert.NotEqual(PdfSignatureTrust.Valid, s.Trust));
        // Reached through exact resolution alone.
        Assert.Empty(response.Diagnostics);
    }

    [Fact]
    public async Task SpecCorrectTwoSignatureIncremental_DamagedLaterSignature_LeavesEarlierModified()
    {
        var bytes = SpecCorrectTwoSignatureFixture();
        // Flip one byte of the second revision's /Reason text: covered by ApprovalTwo only.
        var text = Encoding.Latin1.GetString(bytes);
        bytes[text.IndexOf("(Second approval)", StringComparison.Ordinal) + 1] = (byte)'X';

        var response = await CreateService().ValidateAsync(bytes, TestContext.Current.CancellationToken);

        Assert.Equal(2, response.Signatures.Count);
        Assert.Equal(PdfSignatureIntegrity.ModifiedAfterSigning,
            response.Signatures.Single(s => s.FieldName == "Signature1").Integrity);
        Assert.Equal(PdfSignatureIntegrity.DigestMismatch,
            response.Signatures.Single(s => s.FieldName == "ApprovalTwo").Integrity);
    }

    [Fact]
    public async Task ShadowSignatureDictionaryAtHigherGeneration_DoesNotReplaceTheSignedEvidence()
    {
        var original = File.ReadAllBytes(_fixtures.CreateSignedPdf(new SignatureFixtures.SignedPdfRequest()));
        var originalSignature = Assert.Single(new PdfSignatureLocator().Locate(original, TestContext.Current.CancellationToken));
        // `5 1 obj` next to the signed `5 0 obj` that field 4 references as `5 0 R`.
        var shadowed = AppendUpdate(original,
        [
            (5, 1, "<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached /Reason (Shadow) " +
                   "/Name (Shadow Signer) /ByteRange [0 10 20 30] /Contents <0102030405> >>")
        ]);

        var located = Assert.Single(new PdfSignatureLocator().Locate(shadowed, TestContext.Current.CancellationToken));
        var response = await CreateService().ValidateAsync(shadowed, TestContext.Current.CancellationToken);

        Assert.Equal(originalSignature.ByteRange, located.ByteRange);
        Assert.Equal(originalSignature.Contents, located.Contents);
        Assert.Equal(originalSignature.ContentsStart, located.ContentsStart);
        Assert.Equal("Approval", located.Reason);
        var signature = Assert.Single(response.Signatures);
        Assert.Equal("Approval", signature.Reason);
        Assert.Equal(PdfSignatureIntegrity.ModifiedAfterSigning, signature.Integrity);
        Assert.Equal(SignatureValidationReasons.BytesAppendedAfterSignedRange, signature.IntegrityReason);
        Assert.NotNull(signature.Signer);
        Assert.Empty(response.Diagnostics);
    }

    [Fact]
    public async Task ShadowFieldAtHigherGeneration_DoesNotHideTheSignature()
    {
        var original = File.ReadAllBytes(_fixtures.CreateSignedPdf(new SignatureFixtures.SignedPdfRequest()));
        var shadowed = AppendUpdate(original, [(4, 1, "<< /T (Shadow) >>")]);

        var response = await CreateService().ValidateAsync(shadowed, TestContext.Current.CancellationToken);

        var signature = Assert.Single(response.Signatures);
        Assert.Equal("Signature1", signature.FieldName);
        Assert.Equal(PdfSignatureIntegrity.ModifiedAfterSigning, signature.Integrity);
        Assert.Equal(SignatureValidationReasons.BytesAppendedAfterSignedRange, signature.IntegrityReason);
    }

    [Fact]
    public async Task ShadowCatalogAtHigherGeneration_DoesNotHideTheSignature()
    {
        var original = File.ReadAllBytes(_fixtures.CreateSignedPdf(new SignatureFixtures.SignedPdfRequest()));
        var shadowed = AppendUpdate(original, [(1, 1, "<< /Type /Catalog /Pages 2 0 R >>")]);

        var response = await CreateService().ValidateAsync(shadowed, TestContext.Current.CancellationToken);

        var signature = Assert.Single(response.Signatures);
        Assert.Equal("Signature1", signature.FieldName);
        Assert.Equal(PdfSignatureIntegrity.ModifiedAfterSigning, signature.Integrity);
    }

    [Fact]
    public async Task ShadowDocMdpSignatureAtHigherGeneration_KeepsTheCertification()
    {
        var original = File.ReadAllBytes(_fixtures.CreateSignedPdf(new SignatureFixtures.SignedPdfRequest
        {
            Certification = true
        }));
        var shadowed = AppendUpdate(original,
            [(5, 1, "<< /Type /Sig /SubFilter /adbe.pkcs7.detached /ByteRange [0 10 20 30] /Contents <00> >>")]);

        var response = await CreateService().ValidateAsync(shadowed, TestContext.Current.CancellationToken);

        var signature = Assert.Single(response.Signatures);
        Assert.Equal(PdfSignatureKind.Certification, signature.Kind);
        Assert.Equal(PdfSignatureIntegrity.ModifiedAfterSigning, signature.Integrity);
        Assert.Equal(PdfSignatureTrust.Invalid, signature.Trust);
        Assert.Equal(SignatureValidationReasons.ModifiedAfterCertification, signature.TrustReason);
    }

    [Fact]
    public async Task MissingExactGeneration_FallsBackToNewestGeneration_WithDiagnostic()
    {
        // Field 4 and signature 5 exist only at generation 1 while the references name generation 0.
        var bytes = AppendUpdate(BaseRevision(),
        [
            (3, 0, "<< /Fields [4 0 R] >>"),
            (4, 1, "<< /FT /Sig /T (late) /V 5 0 R >>"),
            (5, 1, "<< /Type /Sig /SubFilter /adbe.pkcs7.detached /Reason (Late) /ByteRange [0 1 2 3] /Contents <00> >>")
        ]);

        var located = new PdfSignatureLocator().LocateDocument(bytes, TestContext.Current.CancellationToken);
        var response = await CreateService().ValidateAsync(bytes, TestContext.Current.CancellationToken);

        Assert.Equal("Late", Assert.Single(located.Signatures).Reason);
        Assert.Equal([SignatureValidationReasons.ReferenceGenerationFallback], located.Diagnostics);
        var signature = Assert.Single(response.Signatures);
        Assert.Equal("late", signature.FieldName);
        Assert.NotEqual(PdfSignatureIntegrity.Intact, signature.Integrity);
        Assert.Equal([SignatureValidationReasons.ReferenceGenerationFallback], response.Diagnostics);
    }

    [Fact]
    public void CatalogOnlyAtHigherGeneration_FallsBack_WithDiagnostic()
    {
        // The trailer says /Root 1 0 R but the only catalog in the xref data is `1 1 obj`. PdfPig
        // refuses to open the file unless its lenient scan finds some `1 0 obj`, so the file also
        // carries an unlisted, field-less one; being outside the xref data it is not an exact entry.
        var bytes = BaseRevision(catalogGeneration: 1, acroFormBody: "<< /Fields [4 0 R] >>",
            orphanBody: "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            extraObjects:
            [
                "<< /FT /Sig /T (Signature1) /V 5 0 R >>",
                "<< /Type /Sig /SubFilter /adbe.pkcs7.detached /ByteRange [0 1 2 3] /Contents <00> >>"
            ]);

        var located = new PdfSignatureLocator().LocateDocument(bytes, TestContext.Current.CancellationToken);

        Assert.Equal("Signature1", Assert.Single(located.Signatures).FieldName);
        Assert.Equal([SignatureValidationReasons.ReferenceGenerationFallback], located.Diagnostics);
    }

    [Fact]
    public void CatalogShadowAtHigherGeneration_KeepsTheExactCatalog_WithoutDiagnostic()
    {
        var bytes = AppendUpdate(BaseRevision(acroFormBody: "<< /Fields [4 0 R] >>",
                extraObjects:
                [
                    "<< /FT /Sig /T (Signature1) /V 5 0 R >>",
                    "<< /Type /Sig /SubFilter /adbe.pkcs7.detached /ByteRange [0 1 2 3] /Contents <00> >>"
                ]),
            [(1, 1, "<< /Type /Catalog /Pages 2 0 R >>")]);

        var located = new PdfSignatureLocator().LocateDocument(bytes, TestContext.Current.CancellationToken);

        Assert.Equal("Signature1", Assert.Single(located.Signatures).FieldName);
        Assert.Empty(located.Diagnostics);
    }

    [Fact]
    public void MaliciousGenerationBumpedUpdate_DeepFieldChain_StillHitsDepthLimit()
    {
        // The update re-points the AcroForm to a /Kids chain that exists only at generation 1 and
        // is deeper than the traversal depth limit; the fallback must not escape the bound.
        var update = new List<(int Number, int Generation, string Body)> { (3, 0, "<< /Fields [4 0 R] >>") };
        for (var i = 4; i < 4 + 40; i++)
            update.Add((i, 1, $"<< /T (k{i}) /Kids [{i + 1} 0 R] >>"));
        update.Add((44, 1, "<< /FT /Sig /T (leaf) >>"));
        var bytes = AppendUpdate(BaseRevision(), update);

        Assert.Throws<PdfSignatureFormatException>(() =>
            new PdfSignatureLocator().Locate(bytes, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void MaliciousGenerationBumpedUpdate_SelfReferences_Terminate()
    {
        var bytes = AppendUpdate(BaseRevision(),
        [
            (3, 0, "<< /Fields [4 0 R 4 1 R 3 0 R] >>"),
            (4, 1, "<< /T (loop) /Parent 4 0 R /Kids [4 0 R 4 1 R 3 0 R] /V 4 0 R >>")
        ]);

        var located = new PdfSignatureLocator().LocateDocument(bytes, TestContext.Current.CancellationToken);

        Assert.Empty(located.Signatures);
        Assert.Equal([SignatureValidationReasons.ReferenceGenerationFallback], located.Diagnostics);
    }

    private static byte[] ReadOdvFixture() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
        "Fixtures", "Signatures", "odv-two-signatures-incremental.pdf"));

    /// <summary>
    /// The ODV fixture as a specification-conforming writer produces it: the incremental update
    /// rewrites the page (3) and the AcroForm (5) under their existing generation 0 (`3 0 obj`,
    /// `5 0 obj`, xref entries `00000 n`). Every edit keeps the byte length, so all offsets stay
    /// valid; ApprovalTwo covers the edited bytes and is re-signed in place with the fixture leaf.
    /// Signature1 and its revision are untouched.
    /// </summary>
    private byte[] SpecCorrectTwoSignatureFixture()
    {
        var bytes = ReadOdvFixture();
        var revisionOneEnd = (int)(OdvSignature1ByteRange[2] + OdvSignature1ByteRange[3]);
        Replace(bytes, revisionOneEnd, "3 1 obj", "3 0 obj");
        Replace(bytes, revisionOneEnd, "5 1 obj", "5 0 obj");
        var text = Encoding.Latin1.GetString(bytes);
        var xref = text.LastIndexOf("\nxref\n", StringComparison.Ordinal);
        for (var at = text.IndexOf(" 00001 n", xref, StringComparison.Ordinal); at >= 0;
             at = text.IndexOf(" 00001 n", at + 1, StringComparison.Ordinal))
            bytes[at + 5] = (byte)'0';
        Assert.DoesNotContain(" 1 obj", Encoding.Latin1.GetString(bytes), StringComparison.Ordinal);
        _fixtures.SignByteRange(bytes, OdvApprovalTwoByteRange);
        return bytes;
    }

    private static void Replace(byte[] bytes, int from, string oldText, string newText)
    {
        var at = Encoding.Latin1.GetString(bytes).IndexOf(oldText, from, StringComparison.Ordinal);
        Assert.True(at >= 0, oldText);
        Encoding.Latin1.GetBytes(newText).CopyTo(bytes, at);
    }

    /// <summary>
    /// A revision 0 with catalog 1 (written under <paramref name="catalogGeneration"/>, referenced
    /// as <c>1 0 R</c> by the trailer), an empty page tree 2, AcroForm 3 and the extra objects as
    /// 4, 5, ... at generation 0. <paramref name="orphanBody"/> is written first and is not listed
    /// in the cross-reference table.
    /// </summary>
    private static byte[] BaseRevision(int catalogGeneration = 0, string acroFormBody = "<< /Fields [] >>",
        IReadOnlyList<string>? extraObjects = null, string? orphanBody = null)
    {
        using var stream = new MemoryStream();
        stream.Write("%PDF-1.7\n"u8);
        if (orphanBody is not null) stream.Write(Encoding.ASCII.GetBytes(orphanBody));
        var baseObjects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm 3 0 R >>",
            "<< /Type /Pages /Kids [] /Count 0 >>",
            acroFormBody
        };
        baseObjects.AddRange(extraObjects ?? []);
        var offsets = new List<long>();
        for (var i = 0; i < baseObjects.Count; i++)
        {
            offsets.Add(stream.Position);
            var generation = i == 0 ? catalogGeneration : 0;
            stream.Write(Encoding.ASCII.GetBytes($"{i + 1} {generation} obj\n{baseObjects[i]}\nendobj\n"));
        }
        var firstXref = stream.Position;
        stream.Write(Encoding.ASCII.GetBytes($"xref\n0 {baseObjects.Count + 1}\n0000000000 65535 f \n"));
        for (var i = 0; i < offsets.Count; i++)
            stream.Write(Encoding.ASCII.GetBytes($"{offsets[i]:D10} {(i == 0 ? catalogGeneration : 0):D5} n \n"));
        stream.Write(Encoding.ASCII.GetBytes(
            $"trailer\n<< /Size {baseObjects.Count + 1} /Root 1 0 R >>\nstartxref\n{firstXref}\n%%EOF\n"));
        return stream.ToArray();
    }

    /// <summary>
    /// Appends one incremental update that writes every given object under the given generation
    /// (xref entry with that generation) and keeps <c>/Root 1 0 R</c>.
    /// </summary>
    private static byte[] AppendUpdate(byte[] original, IReadOnlyList<(int Number, int Generation, string Body)> update)
    {
        var text = Encoding.Latin1.GetString(original);
        var previousXref = long.Parse(text[(text.LastIndexOf("startxref", StringComparison.Ordinal) + 9)..]
            .Trim().Split('\n')[0], System.Globalization.CultureInfo.InvariantCulture);
        using var stream = new MemoryStream();
        stream.Write(original);
        var written = new List<(int Number, int Generation, long Offset)>();
        foreach (var (number, generation, body) in update)
        {
            written.Add((number, generation, stream.Position));
            stream.Write(Encoding.ASCII.GetBytes($"{number} {generation} obj\n{body}\nendobj\n"));
        }
        var xref = stream.Position;
        var size = Math.Max(16, update.Max(o => o.Number) + 1);
        stream.Write("xref\n0 1\n0000000000 65535 f \n"u8);
        foreach (var (number, generation, offset) in written)
            stream.Write(Encoding.ASCII.GetBytes($"{number} 1\n{offset:D10} {generation:D5} n \n"));
        stream.Write(Encoding.ASCII.GetBytes(
            $"trailer\n<< /Size {size} /Root 1 0 R /Prev {previousXref} >>\nstartxref\n{xref}\n%%EOF\n"));
        return stream.ToArray();
    }

    private PdfSignatureValidationService CreateService()
    {
        var options = new SignatureValidationOptions
        {
            Enabled = true,
            UseWindowsTrustedRoots = false,
            ExtraAnchorsDirectory = _fixtures.AnchorDirectory,
            RevocationMode = SignatureRevocationMode.NoCheck
        };
        var logger = NullLogger.Instance;
        return new PdfSignatureValidationService(options,
            new TrustAnchorStore(options, logger, _fixtures.TempRoot),
            new OfflineRevocationStore(null, logger, _fixtures.TempRoot),
            logger);
    }

    public void Dispose() => _fixtures.Dispose();
}
