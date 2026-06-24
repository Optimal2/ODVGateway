namespace ODVGateway.Models;

public sealed class OpenDocViewerBundle
{
    public required OpenDocViewerSession Session { get; init; }

    public required IReadOnlyList<OpenDocViewerDocument> Documents { get; init; }

    public required Dictionary<string, object?> Integration { get; init; }
}

public sealed class OpenDocViewerSession
{
    public required string Id { get; init; }

    public string? UserId { get; init; }

    public required string IssuedAt { get; init; }
}

public sealed class OpenDocViewerDocument
{
    public required string DocumentId { get; init; }

    public required string DocumentVersion { get; init; }

    public required IReadOnlyList<OpenDocViewerFile> Files { get; init; }

    public IReadOnlyList<OpenDocViewerMetadataRecord>? Meta { get; init; }

    public IReadOnlyDictionary<string, OpenDocViewerMetadataRecord>? MetaById { get; init; }

    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    public IReadOnlyDictionary<string, OpenDocViewerMetadataAliasDetail>? MetadataDetails { get; init; }
}

public sealed class OpenDocViewerFile
{
    public required string Id { get; init; }

    public required string Url { get; init; }

    public string? Ext { get; init; }

    public required string DisplayName { get; init; }

    public required string ContentType { get; init; }

    public required int FileNumber { get; init; }

    public string? SourceKind { get; init; }

    public int? PageCountHint { get; init; }

    public string? PageCountHintSource { get; init; }

    public long? SourceSizeBytes { get; init; }

    public string? InlineBase64 { get; init; }

    public string? InlineMimeType { get; init; }

    public long? InlineSizeBytes { get; init; }
}

public sealed class OpenDocViewerMetadataRecord
{
    public required string Id { get; init; }

    public required string Key { get; init; }

    public string? Value { get; init; }

    public string? LookupValue { get; init; }

    public string? RawValue { get; init; }

    public string? RawLookupValue { get; init; }

    public string? Label { get; init; }
}

public sealed class OpenDocViewerMetadataAliasDetail
{
    public required string Alias { get; init; }

    public required string FieldId { get; init; }

    public required string SelectedValue { get; init; }

    public required string SelectedSource { get; init; }

    public string? Value { get; init; }

    public string? LookupValue { get; init; }

    public string? RawValue { get; init; }

    public string? RawLookupValue { get; init; }

    public string? Label { get; init; }

    public string? Type { get; init; }

    public string[]? Contexts { get; init; }
}
