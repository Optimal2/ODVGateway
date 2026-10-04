using System.Text.Json;
using System.Text.Json.Serialization;

namespace ODVGateway.Models;

/// <summary>
/// The shape level 1 (OpenDocViewer) receives from <c>GET /signatures/{sessionKey}/{fileIndex}</c>.
/// Names are pinned with attributes instead of relying on a naming policy, so the wire contract
/// cannot drift when a member is renamed in C#. <c>Diagnostics</c> lists document-level notes about
/// how the PDF was read (for example <c>reference-generation-fallback</c>); it is empty for
/// specification-conforming files.
/// </summary>
public sealed record PdfSignatureValidationResponse(
    IReadOnlyList<PdfSignatureValidation> Signatures,
    DateTimeOffset ValidatedAt,
    [property: JsonPropertyName("diagnostics")] IReadOnlyList<string> Diagnostics);

/// <summary>
/// One signature field of one PDF, as the gateway read it. Fields are null when the document or the
/// signature dictionary does not carry them.
/// </summary>
public sealed record PdfSignatureValidation
{
    /// <summary>The form field path (partial names chained through <c>/Parent</c>).</summary>
    [JsonPropertyName("fieldName")]
    public string? FieldName { get; init; }

    /// <summary>CommonName of the signer certificate subject.</summary>
    [JsonPropertyName("signer")]
    public string? Signer { get; init; }

    /// <summary>Organization (O) of the signer subject, falling back to the issuer's organization.</summary>
    [JsonPropertyName("signerOrganization")]
    public string? SignerOrganization { get; init; }

    /// <summary>CommonName of the signer certificate issuer.</summary>
    [JsonPropertyName("issuer")]
    public string? Issuer { get; init; }

    /// <summary>Certificate serial number, uppercase hex without separators.</summary>
    [JsonPropertyName("serial")]
    public string? Serial { get; init; }

    /// <summary>Certificate validity start.</summary>
    [JsonPropertyName("notBefore")]
    public DateTimeOffset? NotBefore { get; init; }

    /// <summary>Certificate validity end.</summary>
    [JsonPropertyName("notAfter")]
    public DateTimeOffset? NotAfter { get; init; }

    /// <summary>Best claimed signing instant; see <see cref="SigningTimeSource"/>.</summary>
    [JsonPropertyName("signingTime")]
    public DateTimeOffset? SigningTime { get; init; }

    /// <summary>Where <see cref="SigningTime"/> came from.</summary>
    [JsonPropertyName("signingTimeSource")]
    [JsonConverter(typeof(SigningTimeSourceConverter))]
    public PdfSigningTimeSource? SigningTimeSource { get; init; }

    /// <summary>The signature dictionary's <c>/Reason</c>.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    /// <summary>The signature dictionary's <c>/Location</c>.</summary>
    [JsonPropertyName("location")]
    public string? Location { get; init; }

    /// <summary>The signature dictionary's <c>/SubFilter</c>.</summary>
    [JsonPropertyName("subFilter")]
    public string? SubFilter { get; init; }

    /// <summary>Signature role as read from the document.</summary>
    [JsonPropertyName("kind")]
    [JsonConverter(typeof(EnumKebabCaseConverter<PdfSignatureKind>))]
    public PdfSignatureKind Kind { get; init; }

    /// <summary>Does the signed byte range still describe this exact file?</summary>
    [JsonPropertyName("integrity")]
    [JsonConverter(typeof(EnumKebabCaseConverter<PdfSignatureIntegrity>))]
    public PdfSignatureIntegrity Integrity { get; init; }

    /// <summary>Stable machine code qualifying <see cref="Integrity"/>.</summary>
    [JsonPropertyName("integrityReason")]
    public string? IntegrityReason { get; init; }

    /// <summary>
    /// True when the signed ranges cover the whole file apart from the signature placeholder.
    /// Always false for approval signatures, which by design do not cover later increments.
    /// </summary>
    [JsonPropertyName("coversWholeFile")]
    public bool? CoversWholeFile { get; init; }

    /// <summary>Can the signer certificate be trusted at the validation time?</summary>
    [JsonPropertyName("trust")]
    [JsonConverter(typeof(EnumKebabCaseConverter<PdfSignatureTrust>))]
    public PdfSignatureTrust Trust { get; init; }

    /// <summary>Stable machine code for <see cref="Trust"/>.</summary>
    [JsonPropertyName("trustReason")]
    public string? TrustReason { get; init; }

    /// <summary>
    /// The instant the signature was evaluated at: the timestamp token's generation time when the
    /// signature carries a verifiable RFC 3161 token, otherwise the gateway's clock.
    /// </summary>
    [JsonPropertyName("validationTime")]
    public required DateTimeOffset ValidationTime { get; init; }
}

/// <summary>Signature role as the document presents it.</summary>
public enum PdfSignatureKind
{
    Approval,
    Certification,
    Timestamp
}

/// <summary>Result of the byte-level check.</summary>
public enum PdfSignatureIntegrity
{
    Intact,
    ModifiedAfterSigning,
    DigestMismatch,
    SignatureInvalid,
    Unsupported,
    Unreadable
}

/// <summary>Result of the certificate-level check.</summary>
public enum PdfSignatureTrust
{
    Valid,
    Invalid,
    Unknown
}

/// <summary>Provenance of the reported signing time, strongest evidence first.</summary>
public enum PdfSigningTimeSource
{
    /// <summary>An RFC 3161 signature timestamp token verified against the signer's signature.</summary>
    Timestamp,

    /// <summary>The CMS signed attribute <c>signingTime</c>: written by the signer, not proven.</summary>
    SignedAttribute,

    /// <summary>The PDF signature dictionary's <c>/M</c> entry.</summary>
    PdfModificationDate,

    /// <summary>Neither of the above was present or readable.</summary>
    None
}

/// <summary>
/// Writes enum members in the contract's spelling: lower case with hyphens at word boundaries
/// (<c>ModifiedAfterSigning</c> becomes <c>modified-after-signing</c>).
/// </summary>
public sealed class EnumKebabCaseConverter<TEnum> : JsonConverter<TEnum> where TEnum : struct, Enum
{
    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString();
        if (text is null)
        {
            throw new JsonException($"Expected a string for {typeof(TEnum).Name}.");
        }

        foreach (var value in Enum.GetValues<TEnum>())
        {
            if (string.Equals(Format(value.ToString()), text, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        throw new JsonException($"Unknown {typeof(TEnum).Name} value: {text}");
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options) =>
        writer.WriteStringValue(Format(value.ToString()));

    /// <summary>The single place the wire spelling of a member is defined.</summary>
    private static string Format(string memberName)
    {
        var builder = new System.Text.StringBuilder(memberName.Length + 4);
        for (var i = 0; i < memberName.Length; i++)
        {
            var c = memberName[i];
            if (char.IsUpper(c))
            {
                if (i > 0)
                {
                    builder.Append('-');
                }

                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}

/// <summary>
/// The signing time source needs its own converter: <c>pdf-M</c> is not derivable from the member
/// name, and the value set is fixed by the data contract.
/// </summary>
public sealed class SigningTimeSourceConverter : JsonConverter<PdfSigningTimeSource>
{
    public override PdfSigningTimeSource Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetString() switch
        {
            "timestamp" => PdfSigningTimeSource.Timestamp,
            "signed-attribute" => PdfSigningTimeSource.SignedAttribute,
            "pdf-M" => PdfSigningTimeSource.PdfModificationDate,
            "none" => PdfSigningTimeSource.None,
            var other => throw new JsonException("Unknown signingTimeSource value: " + other)
        };

    public override void Write(Utf8JsonWriter writer, PdfSigningTimeSource value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch
        {
            PdfSigningTimeSource.Timestamp => "timestamp",
            PdfSigningTimeSource.SignedAttribute => "signed-attribute",
            PdfSigningTimeSource.PdfModificationDate => "pdf-M",
            _ => "none"
        });
}

internal static class SignatureValidationJson
{
    /// <summary>
    /// Serializer settings for the signature response. Enum spellings come from the converters on
    /// the model, so only the property naming policy is set here.
    /// </summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false
    };
}
