namespace ODVGateway.Services.Signatures;

/// <summary>
/// Proves the page tree safe to open before PdfPig touches the file. PdfPig 0.1.16 builds the page
/// tree eagerly inside <c>PdfDocument.Open</c> and resolves bare indirect references
/// (<c>4 0 obj 5 0 R endobj</c>) through the unguarded self-recursion
/// <c>DirectObjectFinder.TryGet</c>: one cyclic chain on the <c>/Root</c> -&gt; <c>/Pages</c> -&gt;
/// <c>/Kids</c> path kills the process with a stack overflow that .NET cannot catch. This pre-check
/// walks that same path with a small raw reader over the file bytes — no PdfPig calls — using the
/// same exact (number, generation) resolution with newest-generation fallback as
/// <see cref="PdfSignatureLocator"/>, a visited set, and the same depth bound (32).
/// </summary>
/// <remarks>
/// <para>
/// A proven cycle, depth overrun, malformed <c>/Kids</c> entry or oversized tree fails closed with a
/// named <see cref="PdfSignatureFormatException"/> (<c>page-tree-cyclic</c> / <c>page-tree-too-deep</c>,
/// HTTP 422). Anything the reader cannot parse — cross-reference streams, hybrid files, encrypted
/// files, dangling references, unparseable objects — is left to PdfPig, whose behavior for those
/// shapes is unchanged. The pre-check is therefore a pure filter: it throws, or the file proceeds to
/// <c>PdfDocument.Open</c> exactly as before.
/// </para>
/// <para>
/// Bounds: node depth 32 (shared with the locator), at most 100,000 page-tree dictionaries, and a
/// <c>clamp(fileBytes / 2, 40,000, 32,000,000)</c> work budget. The budget allows four times the
/// locator's touch density because the pre-check tokenizes full values (dictionary entries, array
/// elements, nested arrays such as /MediaBox) where the locator counts one visit per object edge;
/// the pinned 20,000-page file uses about a quarter of it. A page-tree node
/// that references itself is fatal, while a longer already-visited cycle is skipped once, preserving
/// the locator's visited-once contract for page references. Every visited node's <c>/Type</c> chain is
/// proven acyclic too, because the eager page-tree build dereferences it through the same unguarded
/// recursion; <c>/Parent</c> is never dereferenced in lenient mode and is not followed.
/// </para>
/// </remarks>
public static class PdfPageTreePrecheck
{
    /// <summary>Deepest page-tree node or reference-chain hop the pre-check follows.</summary>
    public const int MaxDepth = 32;

    /// <summary>Most page-tree dictionaries visited before the tree is rejected as too large.</summary>
    public const int MaxNodes = 100_000;

    /// <summary>
    /// Walks the trailer's <c>/Root</c> -&gt; <c>/Pages</c> -&gt; <c>/Kids</c> path. Returns the
    /// diagnostics observed along the way (at most
    /// <see cref="SignatureValidationReasons.ReferenceGenerationFallback"/>); throws a named
    /// <see cref="PdfSignatureFormatException"/> when the page tree is proven cyclic, too deep,
    /// malformed or oversized.
    /// </summary>
    public static IReadOnlyList<string> Validate(byte[] fileBytes, CancellationToken cancellationToken = default) =>
        new Walker(fileBytes, cancellationToken).Run();

    private sealed record Resolved(RawValue Value, long Number, long Generation, bool Direct);

    private abstract record RawValue;
    private sealed record RawRef(long Number, long Generation) : RawValue;
    private sealed record RawDict(Dictionary<string, RawValue> Entries) : RawValue;
    private sealed record RawArray(List<RawValue> Elements) : RawValue;
    private sealed record RawNumber(long Value) : RawValue;
    private sealed record RawScalar : RawValue;

    private sealed record Trailer(RawValue? Root, long? Prev, bool HasEncrypt, bool HasXRefStm);

    private ref struct Cursor(int position)
    {
        public int Position = position;
    }

    private sealed class Walker(byte[] bytes, CancellationToken token)
    {
        private readonly Dictionary<(long Number, long Generation), long> offsets = new();
        private readonly HashSet<(long Number, long Generation)> free = new();
        private readonly Dictionary<long, long> newestGenerations = new();
        private readonly HashSet<string> diagnostics = new(StringComparer.Ordinal);
        private readonly int budget = Math.Clamp(bytes.Length / 2, 40000, 32_000_000);
        private int touches;

        internal IReadOnlyList<string> Run()
        {
            if (ResolveRoot(ReadTrailerRoot()) is not { Value: RawDict catalog })
            {
                return [];
            }
            if (!catalog.Entries.TryGetValue("Pages", out var pagesValue))
            {
                return [];
            }
            var pages = ResolveRoot(pagesValue);
            if (pages is not { Value: RawDict pagesRoot })
            {
                return [];
            }
            Walk(pagesRoot, (pages.Number, pages.Generation));
            return diagnostics.ToArray();
        }

        private void Walk(RawDict root, (long Number, long Generation) rootId)
        {
            var visited = new HashSet<(long, long)> { rootId };
            var queue = new Queue<(RawDict Node, (long Number, long Generation) Id, int Depth)>();
            queue.Enqueue((root, rootId, 0));
            var nodes = 0;
            while (queue.Count > 0)
            {
                var (node, id, depth) = queue.Dequeue();
                if (depth > MaxDepth)
                {
                    throw TooDeep("The PDF page tree is too deep");
                }
                if (++nodes > MaxNodes)
                {
                    throw TooDeep("The PDF page tree exceeds the traversal bound");
                }
                Touch();
                CheckType(node);
                if (!node.Entries.TryGetValue("Kids", out var kidsValue))
                {
                    continue;
                }
                if (ResolveKidsValue(kidsValue) is not { } kids)
                {
                    continue;
                }
                foreach (var element in kids.Elements)
                {
                    Touch();
                    if (element is not RawRef kid)
                    {
                        throw new PdfSignatureFormatException(
                            $"The PDF page tree has a malformed /Kids entry ({SignatureValidationReasons.PageTreeCyclic}).");
                    }
                    if (ResolveChain(kid) is not { Value: RawDict kidDict } target)
                    {
                        continue;
                    }
                    var kidId = (target.Number, target.Generation);
                    if (kidId == id)
                    {
                        throw Cyclic();
                    }
                    if (visited.Add(kidId))
                    {
                        queue.Enqueue((kidDict, kidId, depth + 1));
                    }
                }
            }
        }

        private void CheckType(RawDict node)
        {
            if (!node.Entries.TryGetValue("Type", out var type) || type is not RawRef reference)
            {
                return;
            }
            _ = ResolveChain(reference);
        }

        private Resolved? ResolveRoot(RawValue? value) => value switch
        {
            null => null,
            RawDict dict => new Resolved(dict, -1, -1, Direct: true),
            RawRef reference => ResolveChain(reference),
            _ => null
        };

        private RawArray? ResolveKidsValue(RawValue value) => value switch
        {
            RawArray array => array,
            RawRef reference => ResolveChain(reference) switch
            {
                null => null,
                { Value: RawArray array } => array,
                _ => throw new PdfSignatureFormatException(
                    $"The PDF page tree has a malformed /Kids entry ({SignatureValidationReasons.PageTreeCyclic}).")
            },
            _ => throw new PdfSignatureFormatException(
                $"The PDF page tree has a malformed /Kids entry ({SignatureValidationReasons.PageTreeCyclic}).")
        };

        private Resolved? ResolveChain(RawRef start)
        {
            var seen = new HashSet<(long, long)>();
            var (number, generation) = (start.Number, start.Generation);
            for (var hop = 0; ; hop++)
            {
                Touch();
                if (!offsets.TryGetValue((number, generation), out var offset))
                {
                    if (!newestGenerations.TryGetValue(number, out var newest) || newest == generation)
                    {
                        return null;
                    }
                    diagnostics.Add(SignatureValidationReasons.ReferenceGenerationFallback);
                    generation = newest;
                    offset = offsets[(number, generation)];
                }
                if (!seen.Add((number, generation)))
                {
                    throw Cyclic();
                }
                if (hop >= MaxDepth)
                {
                    throw TooDeep("The PDF page tree is too deep");
                }
                if (ReadObject(number, generation, offset) is not { } value)
                {
                    return null;
                }
                if (value is not RawRef next)
                {
                    return new Resolved(value, number, generation, Direct: false);
                }
                (number, generation) = (next.Number, next.Generation);
            }
        }

        private static PdfSignatureFormatException Cyclic() => new(
            $"The PDF page tree is cyclic ({SignatureValidationReasons.PageTreeCyclic}).");

        private static PdfSignatureFormatException TooDeep(string message) => new(
            $"{message} ({SignatureValidationReasons.PageTreeTooDeep}).");

        private void Touch()
        {
            token.ThrowIfCancellationRequested();
            if (++touches > budget)
            {
                throw TooDeep("The PDF page tree exceeds the traversal bound");
            }
        }

        private RawValue? ReadTrailerRoot()
        {
            if (FindStartxref() is not { } start)
            {
                return null;
            }
            var seen = new HashSet<long>();
            var offset = start;
            var root = (RawValue?)null;
            var first = true;
            while (true)
            {
                Touch();
                if (!seen.Add(offset))
                {
                    return null;
                }
                if (ParseClassicSection(offset) is not { } section)
                {
                    return null;
                }
                if (section.HasEncrypt || section.HasXRefStm)
                {
                    return null;
                }
                if (first)
                {
                    root = section.Root;
                    first = false;
                }
                if (section.Prev is null)
                {
                    return root;
                }
                offset = section.Prev.Value;
            }
        }

        private long? FindStartxref()
        {
            const string marker = "startxref";
            long? found = null;
            var i = 0;
            while (i + marker.Length + 2 <= bytes.Length)
            {
                if ((i & 0xFFF) == 0)
                {
                    Touch();
                }
                if (bytes[i] == (byte)'s' && MatchLiteral(i, marker) &&
                    (i == 0 || IsDelimiter(bytes[i - 1])) && IsWhite(bytes[i + marker.Length]))
                {
                    var cursor = new Cursor(i + marker.Length);
                    SkipSpace(ref cursor);
                    if (TryMatchPlainLong(cursor.Position, out var offset, out _) && offset >= 0 && offset < bytes.Length)
                    {
                        found = offset;
                    }
                }
                i++;
            }
            return found;
        }

        private Trailer? ParseClassicSection(long offset)
        {
            if (offset < 0 || offset >= bytes.Length)
            {
                return null;
            }
            var cursor = new Cursor((int)offset);
            SkipSpace(ref cursor);
            if (!TryLiteral(ref cursor, "xref"))
            {
                return null;
            }
            while (true)
            {
                Touch();
                SkipSpace(ref cursor);
                if (TryLiteral(ref cursor, "trailer"))
                {
                    SkipSpace(ref cursor);
                    if (cursor.Position + 1 >= bytes.Length || bytes[cursor.Position] != (byte)'<' ||
                        bytes[cursor.Position + 1] != (byte)'<')
                    {
                        return null;
                    }
                    var trailer = ParseDict(ref cursor, 0, out var complete);
                    if (!complete)
                    {
                        return null;
                    }
                    long? prev = null;
                    if (trailer.Entries.TryGetValue("Prev", out var prevValue))
                    {
                        if (prevValue is not RawNumber(var prevOffset) || prevOffset < 0 || prevOffset >= bytes.Length)
                        {
                            return null;
                        }
                        prev = prevOffset;
                    }
                    trailer.Entries.TryGetValue("Root", out var root);
                    return new Trailer(root, prev, trailer.Entries.ContainsKey("Encrypt"),
                        trailer.Entries.ContainsKey("XRefStm"));
                }
                if (!TryParsePlainLong(ref cursor, out var first) || first < 0)
                {
                    return null;
                }
                SkipSpace(ref cursor);
                if (!TryParsePlainLong(ref cursor, out var count) || count < 0 || count > 1_000_000_000 ||
                    first > long.MaxValue - count)
                {
                    return null;
                }
                ToEndOfLine(ref cursor);
                for (var i = 0L; i < count; i++)
                {
                    Touch();
                    if (!TryParseEntry(ref cursor, out var entryOffset, out var generation, out var inUse))
                    {
                        return null;
                    }
                    var key = (first + i, generation);
                    if (offsets.ContainsKey(key) || free.Contains(key))
                    {
                        continue;
                    }
                    if (inUse)
                    {
                        offsets[key] = entryOffset;
                        if (!newestGenerations.TryGetValue(key.Item1, out var newest) || generation > newest)
                        {
                            newestGenerations[key.Item1] = generation;
                        }
                    }
                    else
                    {
                        free.Add(key);
                    }
                }
            }
        }

        private bool TryParseEntry(ref Cursor cursor, out long offset, out long generation, out bool inUse)
        {
            offset = 0;
            generation = 0;
            inUse = false;
            var end = cursor.Position;
            while (end < bytes.Length && bytes[end] != (byte)'\n')
            {
                end++;
            }
            var slice = new Cursor(cursor.Position);
            if (!TryParsePlainLong(ref slice, out offset, end) || !TryParsePlainLong(ref slice, out generation, end))
            {
                return false;
            }
            SkipSliceSpace(ref slice, end);
            if (slice.Position >= end)
            {
                return false;
            }
            var flag = bytes[slice.Position];
            if (flag != (byte)'n' && flag != (byte)'f')
            {
                return false;
            }
            slice.Position++;
            SkipSliceSpace(ref slice, end);
            if (slice.Position != end)
            {
                return false;
            }
            cursor.Position = end < bytes.Length ? end + 1 : end;
            inUse = flag == (byte)'n';
            return true;
        }

        private RawValue? ReadObject(long number, long generation, long offset)
        {
            Touch();
            if (offset < 0 || offset >= bytes.Length)
            {
                return null;
            }
            var cursor = new Cursor((int)offset);
            SkipSpace(ref cursor);
            if (!TryParsePlainLong(ref cursor, out var foundNumber) || foundNumber != number)
            {
                return null;
            }
            SkipSpace(ref cursor);
            if (!TryParsePlainLong(ref cursor, out var foundGeneration) || foundGeneration != generation)
            {
                return null;
            }
            SkipSpace(ref cursor);
            if (!TryLiteral(ref cursor, "obj"))
            {
                return null;
            }
            SkipSpace(ref cursor);
            return ParseValue(ref cursor, 0);
        }

        private RawValue? ParseValue(ref Cursor cursor, int depth)
        {
            if (depth > MaxDepth)
            {
                return null;
            }
            SkipSpace(ref cursor);
            if (cursor.Position >= bytes.Length)
            {
                return null;
            }
            var b = bytes[cursor.Position];
            if (b == (byte)'/')
            {
                cursor.Position++;
                return ParseNameChars(ref cursor) is null ? null : new RawScalar();
            }
            if (b == (byte)'(')
            {
                return SkipString(ref cursor) ? new RawScalar() : null;
            }
            if (b == (byte)'<')
            {
                if (cursor.Position + 1 < bytes.Length && bytes[cursor.Position + 1] == (byte)'<')
                {
                    return ParseDict(ref cursor, depth, out _);
                }
                return SkipHexString(ref cursor) ? new RawScalar() : null;
            }
            if (b == (byte)'[')
            {
                return ParseArray(ref cursor, depth);
            }
            if (b is (byte)']' or (byte)')' or (byte)'>')
            {
                return null;
            }
            if (TryMatchPlainLong(cursor.Position, out var first, out var firstEnd))
            {
                var probe = new Cursor(firstEnd);
                SkipSpace(ref probe);
                if (TryMatchPlainLong(probe.Position, out var second, out var secondEnd))
                {
                    probe.Position = secondEnd;
                    SkipSpace(ref probe);
                    if (TryLiteral(ref probe, "R"))
                    {
                        cursor.Position = probe.Position;
                        return new RawRef(first, second);
                    }
                }
                cursor.Position = firstEnd;
                return new RawNumber(first);
            }
            return ConsumeWord(ref cursor) ? new RawScalar() : null;
        }

        private RawDict ParseDict(ref Cursor cursor, int depth, out bool complete)
        {
            complete = false;
            cursor.Position += 2;
            var entries = new Dictionary<string, RawValue>(StringComparer.Ordinal);
            while (true)
            {
                SkipSpace(ref cursor);
                if (cursor.Position >= bytes.Length)
                {
                    return new RawDict(entries);
                }
                if (bytes[cursor.Position] == (byte)'>')
                {
                    cursor.Position++;
                    SkipSpace(ref cursor);
                    if (cursor.Position < bytes.Length && bytes[cursor.Position] == (byte)'>')
                    {
                        cursor.Position++;
                    }
                    complete = true;
                    return new RawDict(entries);
                }
                if (bytes[cursor.Position] != (byte)'/')
                {
                    return new RawDict(entries);
                }
                cursor.Position++;
                if (ParseNameChars(ref cursor) is not { } name)
                {
                    return new RawDict(entries);
                }
                SkipSpace(ref cursor);
                if (ParseValue(ref cursor, depth + 1) is not { } value)
                {
                    return new RawDict(entries);
                }
                entries[name] = value;
                Touch();
            }
        }

        private RawArray ParseArray(ref Cursor cursor, int depth)
        {
            cursor.Position++;
            var elements = new List<RawValue>();
            while (true)
            {
                SkipSpace(ref cursor);
                if (cursor.Position >= bytes.Length)
                {
                    return new RawArray(elements);
                }
                if (bytes[cursor.Position] == (byte)']')
                {
                    cursor.Position++;
                    return new RawArray(elements);
                }
                if (ParseValue(ref cursor, depth + 1) is not { } value)
                {
                    return new RawArray(elements);
                }
                elements.Add(value);
                Touch();
            }
        }

        private string? ParseNameChars(ref Cursor cursor)
        {
            var start = cursor.Position;
            while (cursor.Position < bytes.Length && !IsDelimiter(bytes[cursor.Position]))
            {
                if ((cursor.Position & 0xFFF) == 0)
                {
                    Touch();
                }
                cursor.Position++;
            }
            var raw = new ReadOnlySpan<byte>(bytes, start, cursor.Position - start);
            var chars = new char[raw.Length];
            var length = 0;
            for (var i = 0; i < raw.Length; i++)
            {
                var c = raw[i];
                if (c == (byte)'#')
                {
                    if (i + 2 >= raw.Length || !IsHexDigit(raw[i + 1]) || !IsHexDigit(raw[i + 2]))
                    {
                        return null;
                    }
                    c = (byte)(HexValue(raw[i + 1]) * 16 + HexValue(raw[i + 2]));
                    i += 2;
                }
                chars[length++] = (char)c;
            }
            return new string(chars, 0, length);
        }

        private bool SkipString(ref Cursor cursor)
        {
            cursor.Position++;
            var nesting = 1;
            while (nesting > 0)
            {
                if ((cursor.Position & 0xFFF) == 0)
                {
                    Touch();
                }
                if (cursor.Position >= bytes.Length)
                {
                    return false;
                }
                var b = bytes[cursor.Position++];
                if (b == (byte)'\\')
                {
                    if (cursor.Position < bytes.Length)
                    {
                        cursor.Position++;
                    }
                }
                else if (b == (byte)'(')
                {
                    nesting++;
                }
                else if (b == (byte)')')
                {
                    nesting--;
                }
            }
            return true;
        }

        private bool SkipHexString(ref Cursor cursor)
        {
            cursor.Position++;
            while (true)
            {
                if ((cursor.Position & 0xFFF) == 0)
                {
                    Touch();
                }
                if (cursor.Position >= bytes.Length)
                {
                    return false;
                }
                if (bytes[cursor.Position++] == (byte)'>')
                {
                    return true;
                }
            }
        }

        private bool ConsumeWord(ref Cursor cursor)
        {
            var start = cursor.Position;
            while (cursor.Position < bytes.Length && !IsDelimiter(bytes[cursor.Position]))
            {
                if ((cursor.Position & 0xFFF) == 0)
                {
                    Touch();
                }
                cursor.Position++;
            }
            return cursor.Position > start;
        }

        private void SkipSpace(ref Cursor cursor)
        {
            while (cursor.Position < bytes.Length)
            {
                if ((cursor.Position & 0xFFF) == 0)
                {
                    Touch();
                }
                var b = bytes[cursor.Position];
                if (IsWhite(b))
                {
                    cursor.Position++;
                }
                else if (b == (byte)'%')
                {
                    while (cursor.Position < bytes.Length && bytes[cursor.Position] != (byte)'\n')
                    {
                        if ((cursor.Position & 0xFFF) == 0)
                        {
                            Touch();
                        }
                        cursor.Position++;
                    }
                }
                else
                {
                    break;
                }
            }
        }

        private void ToEndOfLine(ref Cursor cursor)
        {
            while (cursor.Position < bytes.Length && bytes[cursor.Position] != (byte)'\n')
            {
                if ((cursor.Position & 0xFFF) == 0)
                {
                    Touch();
                }
                cursor.Position++;
            }
            if (cursor.Position < bytes.Length)
            {
                cursor.Position++;
            }
        }

        private bool TryParsePlainLong(ref Cursor cursor, out long value)
        {
            if (!TryMatchPlainLong(cursor.Position, out value, out var end) || end <= cursor.Position)
            {
                value = 0;
                return false;
            }
            cursor.Position = end;
            return true;
        }

        private bool TryParsePlainLong(ref Cursor cursor, out long value, int limit)
        {
            value = 0;
            var start = cursor.Position;
            SkipSliceSpace(ref cursor, limit);
            if (!TryMatchPlainLong(cursor.Position, out value, out var end) || end > limit)
            {
                cursor.Position = start;
                return false;
            }
            cursor.Position = end;
            return true;
        }

        private bool TryMatchPlainLong(int position, out long value, out int end)
        {
            value = 0;
            end = position;
            var i = position;
            var negative = false;
            if (i < bytes.Length && bytes[i] is (byte)'+' or (byte)'-')
            {
                negative = bytes[i] == (byte)'-';
                i++;
            }
            var digits = 0;
            var absolute = 0L;
            while (i < bytes.Length && bytes[i] >= (byte)'0' && bytes[i] <= (byte)'9')
            {
                var digit = bytes[i] - (byte)'0';
                if (absolute > (long.MaxValue - digit) / 10)
                {
                    return false;
                }
                absolute = absolute * 10 + digit;
                i++;
                digits++;
            }
            if (digits == 0 || (i < bytes.Length && !IsDelimiter(bytes[i])))
            {
                return false;
            }
            value = negative ? -absolute : absolute;
            end = i;
            return true;
        }

        private void SkipSliceSpace(ref Cursor cursor, int limit)
        {
            while (cursor.Position < limit && bytes[cursor.Position] is 0 or 9 or 12 or 13 or 32)
            {
                cursor.Position++;
            }
        }

        private bool TryLiteral(ref Cursor cursor, string word)
        {
            if (!MatchLiteral(cursor.Position, word))
            {
                return false;
            }
            var after = cursor.Position + word.Length;
            if (after < bytes.Length && !IsDelimiter(bytes[after]))
            {
                return false;
            }
            cursor.Position = after;
            return true;
        }

        private bool MatchLiteral(int position, string word)
        {
            if (position < 0 || word.Length > bytes.Length - position)
            {
                return false;
            }
            for (var i = 0; i < word.Length; i++)
            {
                if (bytes[position + i] != (byte)word[i])
                {
                    return false;
                }
            }
            return true;
        }

        private static bool IsWhite(byte b) => b is 0 or 9 or 10 or 12 or 13 or 32;

        private static bool IsDelimiter(byte b) =>
            IsWhite(b) || b is (byte)'/' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'(' or (byte)')' or (byte)'%';

        private static bool IsHexDigit(byte b) =>
            b is >= (byte)'0' and <= (byte)'9' or >= (byte)'A' and <= (byte)'F' or >= (byte)'a' and <= (byte)'f';

        private static int HexValue(byte b) => b switch
        {
            >= (byte)'0' and <= (byte)'9' => b - '0',
            >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
            _ => b - 'a' + 10
        };
    }
}
