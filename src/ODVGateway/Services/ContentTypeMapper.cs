using Microsoft.Extensions.Options;
using ODVGateway.Options;

namespace ODVGateway.Services;

public sealed class ContentTypeMapper
{
    private readonly IReadOnlyDictionary<string, string> _contentTypes;

    public ContentTypeMapper(IOptions<ODVGatewayOptions> options)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["pdf"] = "application/pdf",
            ["tif"] = "image/tiff",
            ["tiff"] = "image/tiff",
            ["jpg"] = "image/jpeg",
            ["jpeg"] = "image/jpeg",
            ["png"] = "image/png",
            ["bmp"] = "image/bmp",
            ["gif"] = "image/gif",
            ["webp"] = "image/webp"
        };

        foreach (var entry in options.Value.ContentTypes)
        {
            var key = NormalizeExtension(entry.Key);
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(entry.Value))
            {
                map[key] = entry.Value.Trim();
            }
        }

        _contentTypes = map;
    }

    public string GetContentType(string? extension)
    {
        var key = NormalizeExtension(extension);
        return key is not null && _contentTypes.TryGetValue(key, out var contentType)
            ? contentType
            : "application/octet-stream";
    }

    private static string? NormalizeExtension(string? value)
    {
        var normalized = value?.Trim().TrimStart('.').ToLowerInvariant();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
