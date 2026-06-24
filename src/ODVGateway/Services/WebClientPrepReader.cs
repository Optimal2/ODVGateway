using System.Text;
using System.Text.Json;
using ODVGateway.Models;

namespace ODVGateway.Services;

public static class WebClientPrepReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    static WebClientPrepReader()
    {
        JsonOptions.Converters.Add(new FlexibleStringJsonConverter());
    }

    public static async Task<WebClientPrepRequest> ReadAsync(
        HttpRequest request,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        var body = await ReadBodyAsync(request, maxBytes, cancellationToken);
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new InvalidDataException("Prep payload was empty.");
        }

        var prep = JsonSerializer.Deserialize<WebClientPrepRequest>(body, JsonOptions)
            ?? throw new InvalidDataException("Prep payload did not contain a JSON object.");

        return prep;
    }

    private static async Task<string> ReadBodyAsync(
        HttpRequest request,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        if (maxBytes <= 0)
        {
            maxBytes = 50L * 1024L * 1024L;
        }

        var buffer = new byte[64 * 1024];
        await using var stream = new MemoryStream();

        while (true)
        {
            var read = await request.Body.ReadAsync(buffer, cancellationToken);
            if (read <= 0)
            {
                break;
            }

            if (stream.Length + read > maxBytes)
            {
                throw new InvalidDataException($"Prep payload is larger than the configured limit of {maxBytes} bytes.");
            }

            stream.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
