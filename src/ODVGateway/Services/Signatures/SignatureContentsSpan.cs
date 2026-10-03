using System.Globalization;
using System.Text;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Tokens;

namespace ODVGateway.Services.Signatures;

/// <summary>
/// Locates a direct hex Contents value inside the exact xref-resolved signature object. This is
/// deliberately a strict lexical check, not a second PDF object resolver. Ambiguous dictionaries,
/// indirect Contents and compressed signature objects cannot prove a physical exclusion span.
/// </summary>
internal sealed class SignatureContentsSpan(byte[] bytes, int start, CancellationToken cancellationToken)
{
    private int position = start;
    private readonly int limit = (int)Math.Min(bytes.LongLength, (long)start + 1024 * 1024);

    internal static (long Start, long End) Find(byte[] bytes, ObjectToken? obj, long[] byteRange, CancellationToken token)
    {
        if (obj is null || obj.Position.Type != XrefEntryType.File || obj.Position.Value1 < 0 ||
            obj.Position.Value1 >= bytes.Length)
            return (-1, -1);
        try
        {
            var reader = new SignatureContentsSpan(bytes, (int)obj.Position.Value1, token);
            if (reader.Word() != obj.Number.ObjectNumber.ToString(CultureInfo.InvariantCulture) ||
                reader.Word() != obj.Number.Generation.ToString(CultureInfo.InvariantCulture) || reader.Word() != "obj")
                return (-1, -1);
            reader.Space();
            reader.Expect('<');
            reader.Expect('<');
            var keys = new HashSet<string>(StringComparer.Ordinal);
            (long Start, long End) contents = (-1, -1);
            while (true)
            {
                reader.Space();
                if (reader.Peek() == '>')
                {
                    reader.Expect('>'); reader.Expect('>');
                    break;
                }
                var name = reader.Name();
                if (!keys.Add(name)) return (-1, -1);
                reader.Space();
                var valueStart = reader.position;
                if (name == "ByteRange")
                {
                    if (byteRange.Length != 4) return (-1, -1);
                    reader.Expect('[');
                    foreach (var expected in byteRange)
                    {
                        if (!long.TryParse(reader.Word(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture,
                            out var actual) || actual < 0 || actual != expected) return (-1, -1);
                    }
                    reader.Space(); reader.Expect(']');
                }
                else if (name == "Contents" && reader.Peek() == '<' && reader.position + 1 < reader.limit &&
                    bytes[reader.position + 1] != '<')
                {
                    reader.Value(0);
                    contents = (valueStart, reader.position);
                }
                else reader.Value(0);
            }
            return reader.Word() == "endobj" ? contents : (-1, -1);
        }
        catch (FormatException) { return (-1, -1); }
    }

    private byte Peek()
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (position >= limit) throw new FormatException();
        return bytes[position];
    }

    private void Expect(char c)
    {
        if (Peek() != c) throw new FormatException();
        position++;
    }

    private static bool White(byte b) => b is 0 or 9 or 10 or 12 or 13 or 32;
    private static bool Delimiter(byte b) => White(b) || b is (byte)'/' or (byte)'<' or (byte)'>' or
        (byte)'[' or (byte)']' or (byte)'(' or (byte)')' or (byte)'%';

    private void Space()
    {
        while (position < limit)
        {
            if (White(Peek())) position++;
            else if (Peek() == '%')
            {
                while (position < limit && Peek() is not (10 or 13)) position++;
            }
            else break;
        }
    }

    private string Word()
    {
        Space();
        var first = position;
        while (position < limit && !Delimiter(Peek())) position++;
        if (first == position) throw new FormatException();
        return Encoding.ASCII.GetString(bytes, first, position - first);
    }

    private string Name()
    {
        Expect('/');
        var name = new StringBuilder();
        while (position < limit && !Delimiter(Peek()))
        {
            var b = Peek(); position++;
            if (b == '#')
            {
                var high = Hex(Peek()); position++;
                var low = Hex(Peek()); position++;
                b = (byte)(high * 16 + low);
            }
            name.Append((char)b);
        }
        return name.ToString();
    }

    private static int Hex(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        _ => throw new FormatException()
    };

    private void Value(int depth)
    {
        if (depth > 32) throw new FormatException();
        Space();
        switch (Peek())
        {
            case (byte)'/': Name(); return;
            case (byte)'(':
                position++;
                var nesting = 1;
                while (nesting > 0)
                {
                    var b = Peek(); position++;
                    if (b == '\\') { Peek(); position++; }
                    else if (b == '(') nesting++;
                    else if (b == ')') nesting--;
                }
                return;
            case (byte)'[':
                position++; Space();
                while (Peek() != ']') { Value(depth + 1); Space(); }
                position++; return;
            case (byte)'<':
                position++;
                if (Peek() == '<')
                {
                    position++; Space();
                    var keys = new HashSet<string>(StringComparer.Ordinal);
                    while (Peek() != '>')
                    {
                        if (!keys.Add(Name())) throw new FormatException();
                        Value(depth + 1); Space();
                    }
                    Expect('>'); Expect('>');
                }
                else
                {
                    while (Peek() != '>')
                    {
                        if (!White(Peek())) _ = Hex(Peek());
                        position++;
                    }
                    position++;
                }
                return;
            default:
                var word = Word();
                // An indirect reference is one value, not three separate dictionary entries.
                if (long.TryParse(word, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                {
                    var saved = position;
                    Space();
                    if (position < limit && Peek() >= '0' && Peek() <= '9')
                    {
                        var second = Word(); Space();
                        if (long.TryParse(second, NumberStyles.None, CultureInfo.InvariantCulture, out _) && Peek() == 'R')
                        { position++; return; }
                    }
                    position = saved;
                }
                return;
        }
    }
}
