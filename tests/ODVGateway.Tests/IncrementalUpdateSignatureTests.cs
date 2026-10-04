using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using ODVGateway.Models;
using ODVGateway.Options;
using ODVGateway.Services.Signatures;
using ODVGateway.Tests.Signatures;

namespace ODVGateway.Tests;

/// <summary>
/// Signatures added by incremental updates whose rewritten objects carry a new generation number
/// while older references still name generation 0. See Fixtures/Signatures/README.md.
/// </summary>
public sealed class IncrementalUpdateSignatureTests : IDisposable
{
    private readonly SignatureFixtures _fixtures = new();

    [Fact]
    public async Task OdvTwoSignatureFixture_ReportsBothSignatures()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "Signatures", "odv-two-signatures-incremental.pdf"));

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
        // The generator's throwaway CA is never an anchor here.
        Assert.All(response.Signatures, s => Assert.NotEqual(PdfSignatureTrust.Valid, s.Trust));
    }

    [Fact]
    public async Task OdvTwoSignatureFixture_DamagedLaterSignature_LeavesEarlierModified()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "Signatures", "odv-two-signatures-incremental.pdf"));
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
    public void MaliciousGenerationBumpedUpdate_DeepFieldChain_StillHitsDepthLimit()
    {
        // The update re-points the AcroForm through a generation-bumped object to a /Kids chain
        // deeper than the traversal depth limit; the newer revision must not escape the bound.
        var update = new List<(int Number, string Body)> { (3, "<< /Fields [4 0 R] >>") };
        for (var i = 4; i < 4 + 40; i++)
            update.Add((i, $"<< /T (k{i}) /Kids [{i + 1} 0 R] >>"));
        update.Add((44, "<< /FT /Sig /T (leaf) >>"));
        var bytes = GenerationBumpedUpdate(update);

        Assert.Throws<PdfSignatureFormatException>(() =>
            new PdfSignatureLocator().Locate(bytes, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void MaliciousGenerationBumpedUpdate_SelfReferences_Terminate()
    {
        var bytes = GenerationBumpedUpdate(
        [
            (3, "<< /Fields [4 0 R 4 1 R 3 0 R] >>"),
            (4, "<< /T (loop) /Parent 4 0 R /Kids [4 0 R 4 1 R 3 0 R] /V 4 0 R >>")
        ]);

        Assert.Empty(new PdfSignatureLocator().Locate(bytes, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A one-page revision 0 (catalog 1, pages 2, AcroForm 3 with no fields), followed by one
    /// incremental update that writes every given object with generation 1 while all references
    /// in the file keep naming generation 0.
    /// </summary>
    private static byte[] GenerationBumpedUpdate(IReadOnlyList<(int Number, string Body)> update)
    {
        using var stream = new MemoryStream();
        stream.Write("%PDF-1.7\n"u8);
        var baseObjects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm 3 0 R >>",
            "<< /Type /Pages /Kids [] /Count 0 >>",
            "<< /Fields [] >>"
        };
        var offsets = new List<long>();
        for (var i = 0; i < baseObjects.Length; i++)
        {
            offsets.Add(stream.Position);
            stream.Write(Encoding.ASCII.GetBytes($"{i + 1} 0 obj\n{baseObjects[i]}\nendobj\n"));
        }
        var firstXref = stream.Position;
        stream.Write(Encoding.ASCII.GetBytes($"xref\n0 4\n0000000000 65535 f \n"));
        foreach (var offset in offsets) stream.Write(Encoding.ASCII.GetBytes($"{offset:D10} 00000 n \n"));
        stream.Write(Encoding.ASCII.GetBytes($"trailer\n<< /Size 4 /Root 1 0 R >>\nstartxref\n{firstXref}\n%%EOF\n"));

        var updated = new List<(int Number, long Offset)>();
        foreach (var (number, body) in update)
        {
            updated.Add((number, stream.Position));
            stream.Write(Encoding.ASCII.GetBytes($"{number} 1 obj\n{body}\nendobj\n"));
        }
        var xref = stream.Position;
        var size = Math.Max(4, update.Max(o => o.Number) + 1);
        stream.Write("xref\n0 1\n0000000000 65535 f \n"u8);
        foreach (var (number, offset) in updated)
            stream.Write(Encoding.ASCII.GetBytes($"{number} 1\n{offset:D10} 00001 n \n"));
        stream.Write(Encoding.ASCII.GetBytes(
            $"trailer\n<< /Size {size} /Root 1 0 R /Prev {firstXref} >>\nstartxref\n{xref}\n%%EOF\n"));
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
