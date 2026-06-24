using System.Text;
using System.Text.Json;
using ODVGateway.Models;

namespace ODVGateway.Services;

public static class WebClientSessionDataDecoder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    static WebClientSessionDataDecoder()
    {
        JsonOptions.Converters.Add(new FlexibleStringJsonConverter());
    }

    public static WebClientSessionData Decode(string token)
    {
        var bytes = Convert.FromBase64String(NormalizeBase64(token));
        var json = Encoding.UTF8.GetString(bytes);
        return JsonSerializer.Deserialize<WebClientSessionData>(json, JsonOptions)
            ?? throw new InvalidDataException("Decoded sessiondata was empty.");
    }

    private static string NormalizeBase64(string token)
    {
        var normalized = token.Trim().Replace(' ', '+').Replace('-', '+').Replace('_', '/');
        var padding = normalized.Length % 4;
        return padding switch
        {
            2 => normalized + "==",
            3 => normalized + "=",
            _ => normalized
        };
    }
}
