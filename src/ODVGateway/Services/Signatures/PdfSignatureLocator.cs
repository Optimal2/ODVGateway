using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Tokens;

namespace ODVGateway.Services.Signatures;

/// <summary>
/// Reads signature dictionaries out of a PDF without interpreting the document: the catalog's
/// <c>/AcroForm/Fields</c> tree, the page annotation arrays, and the <c>/Perms</c> markers. Every
/// object lookup goes through the parser's cross-reference data, so classic cross-reference tables,
/// cross-reference streams, object streams and incremental updates are all handled by the library.
/// </summary>
public sealed class PdfSignatureLocator
{
    private const int MaxDepth = 32;

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
        bool IsCertification);

    /// <summary>
    /// Collects every signature dictionary reachable from the document, de-duplicated by object
    /// reference so a field listed both in the AcroForm and in a page annotation is reported once.
    /// </summary>
    public IReadOnlyList<SignatureDictionary> Locate(byte[] fileBytes)
    {
        using var document = OpenDocument(fileBytes);
        var catalog = document.Structure.Catalog.CatalogDictionary;
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

            foreach (var annotation in EnumerateDictionaries(document, annotationsToken))
            {
                TryAddSignature(document, annotation, found, seen, certificationReferences, fieldName: null);
            }
        }

        return found;
    }

    /// <summary>
    /// Opens the document with lenient parsing (real-world signed PDFs frequently carry slightly
    /// off-by-one xref entries) but refuses encrypted documents: the gateway has no password.
    /// </summary>
    public static PdfDocument OpenDocument(byte[] fileBytes)
    {
        var document = PdfDocument.Open(fileBytes, new ParsingOptions
        {
            UseLenientParsing = true,
            SkipMissingFonts = true,
            ClipPaths = false
        });
        if (document.IsEncrypted)
        {
            document.Dispose();
            throw new PdfEncryptedException("The PDF is encrypted; signature validation needs the document in the clear.");
        }

        return document;
    }

    private static HashSet<string> ReadCertificationReferences(PdfDocument document, DictionaryToken catalog)
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

    private static IEnumerable<DictionaryToken> EnumerateFieldDictionaries(PdfDocument document, DictionaryToken acroForm)
    {
        if (!acroForm.TryGet(NameToken.Create("Fields"), out var fieldsToken))
        {
            return Array.Empty<DictionaryToken>();
        }

        return EnumerateDictionaries(document, fieldsToken);
    }

    private static IEnumerable<DictionaryToken> EnumeratePageDictionaries(PdfDocument document, DictionaryToken catalog)
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
        queue.Enqueue((pagesRoot, 0));
        while (queue.Count > 0)
        {
            var (node, depth) = queue.Dequeue();
            if (depth > MaxDepth || !node.TryGet(NameToken.Create("Kids"), out var kidsToken))
            {
                if (string.Equals(TypeName(node, "Type"), "Page", StringComparison.Ordinal))
                {
                    result.Add(node);
                }

                continue;
            }

            foreach (var kid in EnumerateDictionaries(document, kidsToken))
            {
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

    private static IEnumerable<DictionaryToken> EnumerateDictionaries(PdfDocument document, IToken? token)
    {
        var array = Resolve<ArrayToken>(document, token);
        if (array is null)
        {
            yield break;
        }

        foreach (var entry in array.Data)
        {
            var dictionary = Resolve<DictionaryToken>(document, entry);
            if (dictionary is not null)
            {
                yield return dictionary;
            }
        }
    }

    private static void TryAddSignature(
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

        var signature = Resolve<DictionaryToken>(document, valueToken);
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

        found.Add(new SignatureDictionary(
            resolvedName,
            ReadInheritedName(document, field, "SubFilter") ?? ReadName(signature, "SubFilter"),
            ReadText(document, signature, "Name"),
            ReadText(document, signature, "Reason"),
            ReadText(document, signature, "Location"),
            ReadText(document, signature, "M"),
            ReadName(signature, "Transform"),
            ReadByteRange(document, signature),
            ReadContents(document, signature),
            valueToken is IndirectReferenceToken indirect && certificationReferences.Contains(ReferenceKey(indirect))));
    }

    private static string ReferenceKey(IndirectReferenceToken token) =>
        token.Data.ObjectNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + ":" + token.Data.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static long[] ReadByteRange(PdfDocument document, DictionaryToken signature)
    {
        if (!signature.TryGet(NameToken.Create("ByteRange"), out var token))
        {
            return Array.Empty<long>();
        }

        var array = Resolve<ArrayToken>(document, token);
        if (array is null)
        {
            return Array.Empty<long>();
        }

        var values = new List<long>(array.Data.Count);
        foreach (var entry in array.Data)
        {
            var numeric = Resolve<NumericToken>(document, entry);
            if (numeric is null)
            {
                return Array.Empty<long>();
            }

            values.Add(Convert.ToInt64(numeric.Data, System.Globalization.CultureInfo.InvariantCulture));
        }

        return values.ToArray();
    }

    private static byte[] ReadContents(PdfDocument document, DictionaryToken signature)
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

    private static string? ReadInheritedName(PdfDocument document, DictionaryToken field, string name)
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

            current = Resolve<DictionaryToken>(document, parentToken);
        }

        return null;
    }

    private static string? ReadFullyQualifiedName(PdfDocument document, DictionaryToken field)
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

            current = Resolve<DictionaryToken>(document, parentToken);
        }

        return parts.Count == 0 ? null : string.Join(".", parts);
    }

    private static string? ReadName(DictionaryToken dictionary, string name) =>
        dictionary.TryGet(NameToken.Create(name), out var token) && token is NameToken nameToken
            ? nameToken.Data
            : null;

    private static string? TypeName(DictionaryToken dictionary, string name) => ReadName(dictionary, name);

    private static string? ReadText(PdfDocument document, DictionaryToken dictionary, string name)
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
    private static bool TryGetDictionary(
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

        var resolved = Resolve<DictionaryToken>(document, token);
        if (resolved is null)
        {
            return false;
        }

        value = resolved;
        return true;
    }

    private static T? Resolve<T>(PdfDocument document, IToken? token) where T : class, IToken
    {
        var current = token;
        for (var depth = 0; depth < MaxDepth; depth++)
        {
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
                    current = document.Structure.GetObject(referenceToken.Data);
                    continue;
                default:
                    return null;
            }
        }

        return null;
    }

}