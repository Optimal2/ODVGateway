using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Tokens;

namespace ODVGateway.Services.Signatures;

/// <summary>
/// Reads signature dictionaries out of a PDF without interpreting the document: the catalog's
/// <c>/AcroForm/Fields</c> tree, the page annotation arrays, and the <c>/Perms</c> markers. Every
/// object lookup goes through the parser's cross-reference data, so classic cross-reference tables,
/// cross-reference streams, object streams and incremental updates are all handled by the library.
/// References are resolved exactly by (number, generation), as the PDF specification and PdfPig do;
/// only a reference whose exact entry is missing falls back to the newest in-use generation of its
/// object number, and that fallback is reported as a diagnostic (see <see cref="Target"/>).
/// </summary>
public sealed class PdfSignatureLocator
{
    private const int MaxDepth = 32;
    // Compressed graphs get bounded headroom while ordinary large page trees scale with bytes.
    private int MaxVisits => Math.Clamp(fileBytes.Length / 8, 10000, 8_000_000);
    private CancellationToken cancellationToken;
    private byte[] fileBytes = [];
    private int visits;
    private readonly Dictionary<string, IToken?> objects = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<IndirectReference, XrefLocation> objectOffsets = new Dictionary<IndirectReference, XrefLocation>();
    private Dictionary<long, int> newestGenerations = new();
    private readonly SortedSet<string> diagnostics = new(StringComparer.Ordinal);

    private void CheckBudget()
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (++visits > MaxVisits)
            throw new PdfSignatureFormatException("The PDF object traversal limit was exceeded.");
    }

    /// <summary>
    /// One signature dictionary as stored in the document, before any cryptographic work.
    /// </summary>
    public sealed record SignatureDictionary(
        string? FieldName,
        string? SubFilter,
        string? Name,
        string? Reason,
        string? Location,
        string? ModificationDate,
        string? Transform,
        long[] ByteRange,
        byte[] Contents,
        bool IsCertification,
        long ContentsStart = -1,
        long ContentsEnd = -1);

    /// <summary>
    /// Collects every signature dictionary reachable from the document, de-duplicated by object
    /// reference so a field listed both in the AcroForm and in a page annotation is reported once.
    /// </summary>
    public IReadOnlyList<SignatureDictionary> Locate(byte[] fileBytes, CancellationToken cancellationToken = default) =>
        LocateDocument(fileBytes, cancellationToken).Signatures;

    /// <summary>
    /// The signature dictionaries of one document plus document-level diagnostics such as
    /// <see cref="SignatureValidationReasons.ReferenceGenerationFallback"/>.
    /// </summary>
    public sealed record LocatedSignatures(IReadOnlyList<SignatureDictionary> Signatures, IReadOnlyList<string> Diagnostics);

    /// <summary>
    /// <see cref="Locate"/> plus the diagnostics collected while walking the object graph.
    /// </summary>
    public LocatedSignatures LocateDocument(byte[] fileBytes, CancellationToken cancellationToken = default) =>
        new PdfSignatureLocator { fileBytes = fileBytes, cancellationToken = cancellationToken }.LocateCore();

    private LocatedSignatures LocateCore()
    {
        CheckBudget();
        using var stream = new CancellablePdfStream(fileBytes, cancellationToken);
        using var document = OpenDocument(stream);
        CheckBudget();
        objectOffsets = document.Structure.CrossReferenceTable.ObjectOffsets;
        newestGenerations = IndexNewestGenerations();
        var catalog = ReadCatalog(document);
        var certificationReferences = ReadCertificationReferences(document, catalog);

        var found = new List<SignatureDictionary>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (TryGetDictionary(document, catalog, "AcroForm", out var acroForm))
        {
            foreach (var field in EnumerateFieldDictionaries(document, acroForm))
            {
                TryAddSignature(document, field, found, seen, certificationReferences, fieldName: null);
            }
        }

        foreach (var page in EnumeratePageDictionaries(document, catalog))
        {
            if (!page.TryGet(NameToken.Create("Annots"), out var annotationsToken))
            {
                continue;
            }

            foreach (var annotation in EnumerateDictionaries(document, annotationsToken, skipDangling: true))
            {
                TryAddSignature(document, annotation, found, seen, certificationReferences, fieldName: null);
            }
        }

        return new LocatedSignatures(found, diagnostics.ToArray());
    }

    /// <summary>
    /// Highest in-use generation per object number in the merged cross-reference data, used only by
    /// the fallback in <see cref="Target"/>. The index is linear in the xref data the parser has
    /// already bounded by the file size.
    /// </summary>
    private Dictionary<long, int> IndexNewestGenerations()
    {
        var index = new Dictionary<long, int>();
        var count = 0;
        foreach (var reference in objectOffsets.Keys)
        {
            if ((++count & 0xFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (!index.TryGetValue(reference.ObjectNumber, out var generation) || reference.Generation > generation)
                index[reference.ObjectNumber] = reference.Generation;
        }

        return index;
    }

    /// <summary>
    /// The object a reference names. An entry with exactly this (number, generation) always wins,
    /// even when the same number also exists under a higher generation: an incremental update that
    /// writes <c>n 1 obj</c> next to a referenced <c>n 0 obj</c> is out of specification (a rewritten
    /// object keeps its generation) and must never shadow the object the reference names. Only when
    /// the exact entry is missing does the reference fall back to the newest in-use generation of
    /// its number, and that is recorded as
    /// <see cref="SignatureValidationReasons.ReferenceGenerationFallback"/>. Never invents an object;
    /// a reference to an unknown number is returned unchanged.
    /// </summary>
    private IndirectReference Target(IndirectReference reference)
    {
        if (objectOffsets.ContainsKey(reference) ||
            !newestGenerations.TryGetValue(reference.ObjectNumber, out var generation) ||
            generation == reference.Generation)
            return reference;

        diagnostics.Add(SignatureValidationReasons.ReferenceGenerationFallback);
        return new IndirectReference(reference.ObjectNumber, generation);
    }

    /// <summary>
    /// The catalog named by the current trailer's <c>/Root</c>, under the same exact-first rule as
    /// every other reference: PdfPig's catalog when the exact entry exists, otherwise the newest
    /// generation of the root's object number (with the fallback diagnostic).
    /// </summary>
    private DictionaryToken ReadCatalog(PdfDocument document)
    {
        var root = document.Structure.Trailer.Root;
        if (Target(root).Equals(root))
            return document.Structure.Catalog.CatalogDictionary;

        return Resolve<DictionaryToken>(document, new IndirectReferenceToken(root))
            ?? document.Structure.Catalog.CatalogDictionary;
    }

    /// <summary>
    /// Opens the document with lenient parsing (real-world signed PDFs frequently carry slightly
    /// off-by-one xref entries) but refuses encrypted documents: the gateway has no password.
    /// </summary>
    public static PdfDocument OpenDocument(byte[] fileBytes) => OpenDocument(new MemoryStream(fileBytes, false));

    private static PdfDocument OpenDocument(Stream stream)
    {
        var document = PdfDocument.Open(stream, new ParsingOptions
        {
            UseLenientParsing = true,
            SkipMissingFonts = true,
            ClipPaths = false,
            MaxStackDepth = MaxDepth
        });
        if (document.IsEncrypted)
        {
            document.Dispose();
            throw new PdfEncryptedException("The PDF is encrypted; signature validation needs the document in the clear.");
        }

        return document;
    }

    private HashSet<string> ReadCertificationReferences(PdfDocument document, DictionaryToken catalog)
    {
        var references = new HashSet<string>(StringComparer.Ordinal);
        if (!catalog.TryGet(NameToken.Create("Perms"), out var permsToken))
        {
            return references;
        }

        var perms = Resolve<DictionaryToken>(document, permsToken);
        if (perms is null)
        {
            return references;
        }

        // /DocMDP is the certification promise. /UR and /SR are permission changes on top of an
        // existing certification and are not treated as a certification signature of their own.
        foreach (var name in new[] { "DocMDP" })
        {
            if (perms.TryGet(NameToken.Create(name), out var token) && token is IndirectReferenceToken reference)
            {
                references.Add(ReferenceKey(reference));
            }
        }

        return references;
    }

    private IEnumerable<DictionaryToken> EnumerateFieldDictionaries(PdfDocument document, DictionaryToken acroForm)
    {
        if (!acroForm.TryGet(NameToken.Create("Fields"), out var fieldsToken))
        {
            yield break;
        }
        var visited = new HashSet<DictionaryToken>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<(DictionaryToken Field, int Depth)>();
        foreach (var field in EnumerateDictionaries(document, fieldsToken, skipDangling: true))
            if (visited.Add(field)) queue.Enqueue((field, 0));
        while (queue.Count > 0)
        {
            CheckBudget();
            var (field, depth) = queue.Dequeue();
            if (depth > MaxDepth) throw new PdfSignatureFormatException("The PDF field tree is too deep.");
            yield return field;
            if (!field.TryGet(NameToken.Create("Kids"), out var kids)) continue;
            foreach (var child in EnumerateDictionaries(document, kids, skipDangling: true))
                if (visited.Add(child)) queue.Enqueue((child, depth + 1));
        }
    }

    private IEnumerable<DictionaryToken> EnumeratePageDictionaries(PdfDocument document, DictionaryToken catalog)
    {
        var pagesRoot = catalog.TryGet(NameToken.Create("Pages"), out var pagesToken)
            ? Resolve<DictionaryToken>(document, pagesToken)
            : null;
        if (pagesRoot is null)
        {
            return Array.Empty<DictionaryToken>();
        }

        var result = new List<DictionaryToken>();
        var queue = new Queue<(DictionaryToken Node, int Depth)>();
        var visited = new HashSet<DictionaryToken>(ReferenceEqualityComparer.Instance) { pagesRoot };
        queue.Enqueue((pagesRoot, 0));
        while (queue.Count > 0)
        {
            CheckBudget();
            var (node, depth) = queue.Dequeue();
            if (depth > MaxDepth) throw new PdfSignatureFormatException("The PDF page tree is too deep.");
            if (!node.TryGet(NameToken.Create("Kids"), out var kidsToken))
            {
                if (string.Equals(TypeName(node, "Type"), "Page", StringComparison.Ordinal))
                {
                    result.Add(node);
                }

                continue;
            }

            // Strict on purpose: a broken page tree must fail the file, not read as empty. Note
            // PdfPig walks this same chain eagerly while opening, so a cycling chain never
            // reaches this call.
            foreach (var kid in EnumerateDictionaries(document, kidsToken))
            {
                if (!visited.Add(kid)) continue;
                if (string.Equals(TypeName(kid, "Type"), "Page", StringComparison.Ordinal))
                {
                    result.Add(kid);
                }
                else
                {
                    queue.Enqueue((kid, depth + 1));
                }
            }
        }

        return result;
    }

    private IEnumerable<DictionaryToken> EnumerateDictionaries(PdfDocument document, IToken? token,
        bool skipDangling = false)
    {
        var array = Resolve<ArrayToken>(document, token, skipDangling);
        if (array is null)
        {
            yield break;
        }

        foreach (var entry in array.Data)
        {
            CheckBudget();
            var dictionary = Resolve<DictionaryToken>(document, entry, skipDangling);
            if (dictionary is not null)
            {
                yield return dictionary;
            }
        }
    }

    private void TryAddSignature(
        PdfDocument document,
        DictionaryToken field,
        List<SignatureDictionary> found,
        HashSet<string> seen,
        IReadOnlySet<string> certificationReferences,
        string? fieldName)
    {
        var resolvedName = fieldName ?? ReadFullyQualifiedName(document, field);
        if (!field.TryGet(NameToken.Create("V"), out var valueToken))
        {
            // A field without /V may hold /DockedAppComponents or a timestamp reference only.
            return;
        }

        CheckBudget();
        var signature = Resolve<DictionaryToken>(document, valueToken, skipDangling: true);
        if (signature is null)
        {
            return;
        }

        var type = TypeName(signature, "Type");
        var fieldType = ReadInheritedName(document, field, "FT");
        var looksLikeSignature = string.Equals(type, "Sig", StringComparison.Ordinal)
            || string.Equals(fieldType, "Sig", StringComparison.Ordinal);
        if (!looksLikeSignature)
        {
            return;
        }

        var key = valueToken is IndirectReferenceToken reference
            ? ReferenceKey(reference)
            : "inline:" + found.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!seen.Add(key))
        {
            return;
        }

        var physicalObject = valueToken is IndirectReferenceToken physicalReference
            ? document.Structure.GetObject(Target(physicalReference.Data)) : null;
        var byteRange = ReadByteRange(document, signature);
        var span = SignatureContentsSpan.Find(fileBytes, physicalObject, byteRange, cancellationToken);
        found.Add(new SignatureDictionary(
            resolvedName,
            ReadInheritedName(document, field, "SubFilter") ?? ReadName(signature, "SubFilter"),
            ReadText(document, signature, "Name"),
            ReadText(document, signature, "Reason"),
            ReadText(document, signature, "Location"),
            ReadText(document, signature, "M"),
            ReadName(signature, "Transform"),
            byteRange,
            ReadContents(document, signature),
            valueToken is IndirectReferenceToken indirect && certificationReferences.Contains(ReferenceKey(indirect)),
            span.Start, span.End));
    }

    private string ReferenceKey(IndirectReferenceToken token)
    {
        var current = Target(token.Data);
        return current.ObjectNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ":" + current.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private long[] ReadByteRange(PdfDocument document, DictionaryToken signature)
    {
        if (!signature.TryGet(NameToken.Create("ByteRange"), out var token))
        {
            return Array.Empty<long>();
        }

        var array = Resolve<ArrayToken>(document, token);
        if (array is null || array.Data.Count != 4)
        {
            return Array.Empty<long>();
        }

        var values = new List<long>(array.Data.Count);
        foreach (var entry in array.Data)
        {
            CheckBudget();
            var numeric = Resolve<NumericToken>(document, entry);
            if (numeric is null || numeric.Data != Math.Truncate(numeric.Data) ||
                numeric.Data < 0 || numeric.Data > fileBytes.LongLength)
            {
                return Array.Empty<long>();
            }

            values.Add(Convert.ToInt64(numeric.Data, System.Globalization.CultureInfo.InvariantCulture));
        }

        return values.ToArray();
    }

    private byte[] ReadContents(PdfDocument document, DictionaryToken signature)
    {
        if (!signature.TryGet(NameToken.Create("Contents"), out var token))
        {
            return Array.Empty<byte>();
        }

        switch (Resolve<IToken>(document, token))
        {
            case HexToken hex:
                return hex.Bytes.ToArray();
            case StringToken text:
                return Encoding.Latin1.GetBytes(text.Data);
            default:
                return Array.Empty<byte>();
        }
    }

    private string? ReadInheritedName(PdfDocument document, DictionaryToken field, string name)
    {
        var current = field;
        for (var depth = 0; current is not null && depth < MaxDepth; depth++)
        {
            var value = ReadName(current, name);
            if (value is not null)
            {
                return value;
            }

            if (!current.TryGet(NameToken.Create("Parent"), out var parentToken))
            {
                return null;
            }

            current = Resolve<DictionaryToken>(document, parentToken, skipDangling: true);
        }

        return null;
    }

    private string? ReadFullyQualifiedName(PdfDocument document, DictionaryToken field)
    {
        var parts = new List<string>();
        var current = field;
        for (var depth = 0; current is not null && depth < MaxDepth; depth++)
        {
            var partial = ReadText(document, current, "T");
            if (!string.IsNullOrEmpty(partial))
            {
                parts.Insert(0, partial);
            }

            if (!current.TryGet(NameToken.Create("Parent"), out var parentToken))
            {
                break;
            }

            current = Resolve<DictionaryToken>(document, parentToken, skipDangling: true);
        }

        return parts.Count == 0 ? null : string.Join(".", parts);
    }

    private string? ReadName(DictionaryToken dictionary, string name) =>
        dictionary.TryGet(NameToken.Create(name), out var token) && token is NameToken nameToken
            ? nameToken.Data
            : null;

    private string? TypeName(DictionaryToken dictionary, string name) => ReadName(dictionary, name);

    private string? ReadText(PdfDocument document, DictionaryToken dictionary, string name)
    {
        if (!dictionary.TryGet(NameToken.Create(name), out var token))
        {
            return null;
        }

        return Resolve<IToken>(document, token) switch
        {
            StringToken text => text.Data,
            NameToken nameToken => nameToken.Data,
            _ => null
        };
    }

    /// <summary>
    /// Follows object wrappers and indirect references until the requested token type appears.
    /// PdfPig hands out <see cref="ObjectToken"/> wrappers for objects stored in object streams and
    /// <see cref="IndirectReferenceToken"/> for classical references; both have to be peeled off.
    /// </summary>
    private bool TryGetDictionary(
        PdfDocument document,
        DictionaryToken dictionary,
        string name,
        out DictionaryToken value)
    {
        value = null!;
        if (!dictionary.TryGet(NameToken.Create(name), out var token))
        {
            return false;
        }

        var resolved = Resolve<DictionaryToken>(document, token, cycleRootName: name);
        if (resolved is null)
        {
            return false;
        }

        value = resolved;
        return true;
    }

    private T? Resolve<T>(PdfDocument document, IToken? token, bool skipDangling = false, string? cycleRootName = null) where T : class, IToken
    {
        var current = token;
        var references = new HashSet<string>(StringComparer.Ordinal);
        for (var depth = 0; depth < MaxDepth; depth++)
        {
            CheckBudget();
            switch (current)
            {
                case null:
                    return null;
                case T direct:
                    return direct;
                case ObjectToken objectToken:
                    current = objectToken.Data;
                    continue;
                case IndirectReferenceToken referenceToken:
                    if (!references.Add(ReferenceKey(referenceToken)))
                    {
                        // A reference cycle inside a field/widget/annotation walk: the element is
                        // skipped, but the skip is reported like every other unresolvable reference.
                        // A cycle in a named strict root instead fails the file: it cannot be
                        // inventoried, so it must never read as absent.
                        if (!skipDangling && cycleRootName is not null)
                            throw new PdfSignatureFormatException($"The PDF {cycleRootName} reference is cyclic.");
                        if (skipDangling)
                            diagnostics.Add(SignatureValidationReasons.ReferenceCycleSkipped);
                        return null;
                    }
                    var key = ReferenceKey(referenceToken);
                    if (!objects.TryGetValue(key, out current))
                    {
                        var target = Target(referenceToken.Data);
                        try
                        {
                            current = document.Structure.GetObject(target);
                        }
                        catch (InvalidOperationException) when (skipDangling && !objectOffsets.ContainsKey(target))
                        {
                            // PdfPig throws for an absent object. Only field/widget/annotation
                            // walks may skip it; roots and other parser failures still fail closed.
                            // Do not cache this as null: a later strict lookup must still fail.
                            diagnostics.Add(SignatureValidationReasons.DanglingReferenceSkipped);
                            return null;
                        }
                        if (current is null && skipDangling)
                        {
                            // The parser returned no object without throwing: still a dangling
                            // reference, still reported. Do not cache this as null: a later
                            // strict lookup must still fail.
                            diagnostics.Add(SignatureValidationReasons.DanglingReferenceSkipped);
                            return null;
                        }
                        objects.Add(key, current);
                    }
                    continue;
                default:
                    // A direct value of the wrong type inside a field/widget/annotation walk:
                    // the element is skipped, but the skip is reported.
                    if (skipDangling)
                        diagnostics.Add(SignatureValidationReasons.UnexpectedObjectTypeSkipped);
                    return null;
            }
        }

        // The chain outlived the depth bound: inside a field/widget/annotation walk the
        // element is skipped, but the skip is reported so a signature hidden behind a
        // long chain cannot vanish with an empty diagnostics array.
        if (skipDangling)
            diagnostics.Add(SignatureValidationReasons.ReferenceDepthExceeded);

        return null;
    }

}
