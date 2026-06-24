using System.Text.Json.Serialization;

namespace ODVGateway.Models;

public sealed class WebClientSessionData
{
    public string? UserId { get; set; }

    public string? SessionId { get; set; }

    public string? AspxAuth { get; set; }

    public List<string> CaseIds { get; set; } = [];
}

public sealed class WebClientPrepRequest
{
    public string? UserId { get; set; }

    public string? SessionId { get; set; }

    public string? AspxAuth { get; set; }

    public List<WebClientPortableDocument> PortableDocuments { get; set; } = [];
}

public sealed class WebClientPortableDocument
{
    public string? DocumentId { get; set; }

    public string? Created { get; set; }

    public string? Modified { get; set; }

    [JsonPropertyName("sourceid")]
    public string? SourceId { get; set; }

    public List<WebClientMetadataField> MetaData { get; set; } = [];

    public List<string> FileData { get; set; } = [];
}

public sealed class WebClientMetadataField
{
    public string? Id { get; set; }

    public string? Value { get; set; }

    public string? LookupValue { get; set; }
}
