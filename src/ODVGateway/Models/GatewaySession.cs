namespace ODVGateway.Models;

public sealed class GatewaySession
{
    public required string SessionKey { get; init; }

    public required string HandoffLookupKey { get; init; }

    public required WebClientPrepRequest Prep { get; init; }

    public required DateTimeOffset CreatedUtc { get; init; }

    public required DateTimeOffset ExpiresUtc { get; init; }

    public required IReadOnlyList<GatewaySourceFile> SourceFiles { get; init; }
}

public sealed class GatewaySourceFile
{
    public required int Index { get; init; }

    public required string DocumentId { get; init; }

    public required int DocumentIndex { get; init; }

    public required int FileNumber { get; init; }

    public string? FileId { get; init; }

    public string? Extension { get; init; }

    public required string FilePath { get; init; }

    public required string DisplayName { get; init; }
}
