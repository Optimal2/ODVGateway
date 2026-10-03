using System.Formats.Asn1;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace ODVGateway.Services.Signatures;

/// <summary>
/// Reads single-valued relative distinguished names out of a certificate name. The framework's
/// <see cref="X500DistinguishedName"/> only formats the whole name, and splitting its string form
/// apart breaks on escaped commas, so the DER is walked instead.
/// </summary>
internal static class X500AttributeValue
{
    public const string CommonNameOid = "2.5.4.3";
    public const string OrganizationOid = "2.5.4.10";
    public const string OrganizationalUnitOid = "2.5.4.11";
    public const string CountryOid = "2.5.4.6";

    /// <summary>
    /// Returns the value of the first RDN using <paramref name="oid"/>, or null when the name does
    /// not contain it or the encoding cannot be decoded.
    /// </summary>
    public static string? Find(X500DistinguishedName name, string oid)
    {
        try
        {
            // Name ::= CHOICE { RDNSequence } → SEQUENCE OF SET OF SEQUENCE { OID, AttributeValue }
            var rdns = new AsnReader(name.RawData, AsnEncodingRules.DER).ReadSequence();
            while (rdns.HasData)
            {
                var set = rdns.ReadSetOf();
                while (set.HasData)
                {
                    var attributeTypeAndValue = set.ReadSequence();
                    var attributeOid = attributeTypeAndValue.ReadObjectIdentifier();
                    if (!string.Equals(attributeOid, oid, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var value = ReadValue(attributeTypeAndValue);
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value.Trim();
                    }
                }
            }
        }
        catch (Exception exception)
            when (exception is AsnContentException or ArgumentException or InvalidOperationException or FormatException)
        {
            return null;
        }

        return null;
    }

    private static string? ReadValue(AsnReader reader)
    {
        var tag = reader.PeekTag();
        var encoded = reader.ReadEncodedValue().ToArray();
        try
        {
            if (tag.TagClass == TagClass.Universal)
            {
                var content = encoded.AsSpan(1 + LengthOfLength(encoded)).ToArray();
                return (UniversalTagNumber)tag.TagValue switch
                {
                    UniversalTagNumber.UTF8String => Encoding.UTF8.GetString(content),
                    UniversalTagNumber.BMPString => Encoding.BigEndianUnicode.GetString(content),
                    UniversalTagNumber.PrintableString => Encoding.ASCII.GetString(content),
                    UniversalTagNumber.IA5String => Encoding.ASCII.GetString(content),
                    UniversalTagNumber.T61String => Encoding.Latin1.GetString(content),
                    _ => Encoding.UTF8.GetString(content)
                };
            }
        }
        catch (Exception exception)
            when (exception is ArgumentException or DecoderFallbackException or IndexOutOfRangeException)
        {
            return null;
        }

        // An unexpected encoding (or a decoding failure) must not change the verdict.
        return null;
    }

    /// <summary>Number of bytes the length field of a DER element occupies after the tag byte.</summary>
    private static int LengthOfLength(ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length < 2)
        {
            return 0;
        }

        var first = encoded[1];
        if (first <= 0x7F)
        {
            return 1;
        }

        var count = first & 0x7F;
        return count is > 0 and < 8 ? 1 + count : 1;
    }
}

/// <summary>
/// Parses the PDF date string format used by the <c>/M</c> entry:
/// <c>D:YYYYMMDDHHmmSS+HH'mm'</c>, with the prefix, the time part and the time zone all optional.
/// A missing zone is reported as unknown rather than guessed, so the caller gets a value anchored to
/// UTC only when the document actually states one.
/// </summary>
internal static class PdfDate
{
    public static bool TryParse(string? text, out DateTimeOffset value, out bool hasTimeZone)
    {
        value = default;
        hasTimeZone = false;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        if (trimmed.StartsWith("D:", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[2..];
        }

        // Split the zone suffix off before reading the digits.
        var zone = (TimeSpan?)null;
        var zoneIndex = trimmed.IndexOfAny(['+', '-'], 4);
        if (zoneIndex > 0)
        {
            var zoneText = trimmed[zoneIndex..].Replace("'", string.Empty, StringComparison.Ordinal);
            trimmed = trimmed[..zoneIndex];
            if (zoneText.Length > 1
                && int.TryParse(zoneText[1..], System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var zoneMinutes))
            {
                // PDF writes the offset in hours and minutes glued together: +0230 means +02:30.
                var hours = zoneMinutes / 100;
                var minutes = zoneMinutes % 100;
                zone = new TimeSpan(hours, minutes, 0);
                if (zoneText[0] == '-')
                {
                    zone = -zone;
                }

                hasTimeZone = true;
            }
            else if (zoneText.EndsWith("Z", StringComparison.OrdinalIgnoreCase))
            {
                zone = TimeSpan.Zero;
                hasTimeZone = true;
            }
        }
        else if (trimmed.EndsWith("Z", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^1];
            zone = TimeSpan.Zero;
            hasTimeZone = true;
        }

        if (trimmed.Length < 4)
        {
            return false;
        }

        // The digits run YYYY MM DD HH mm SS, and a producer may stop at any of them.
        Span<int> fields = [0, 1, 1, 0, 0, 0];
        Span<int> starts = [0, 4, 6, 8, 10, 12];
        for (var i = 0; i < fields.Length; i++)
        {
            if (starts[i] >= trimmed.Length)
            {
                break;
            }

            var width = Math.Min(i == 0 ? 4 : 2, trimmed.Length - starts[i]);
            if (!int.TryParse(trimmed.AsSpan(starts[i], width), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var part))
            {
                return false;
            }

            fields[i] = part;
        }

        try
        {
            var date = new DateTime(fields[0], fields[1], fields[2], fields[3], fields[4], fields[5],
                DateTimeKind.Unspecified);
            value = zone is null
                ? new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Utc))
                : new DateTimeOffset(date, zone.Value).ToUniversalTime();
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }
}
