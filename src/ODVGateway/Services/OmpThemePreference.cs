using System.Text.Json;

namespace ODVGateway.Services;

// Shared OMP light/dark theme preference (theme contract version 1). The
// OMP_THEME_PREFERENCE cookie carries a URI-encoded JSON object
// {"version":1,"mode":"system|light|dark","revision":"..."} written by the
// hosting apps. The gateway only reads the mode for its server-rendered
// status/error pages; the revision orders the cookie against the browser's
// localStorage mirror and is not needed server-side. Anything that does not
// match the exact contract shape falls back to "system".
public static class OmpThemePreference
{
    public const string CookieName = "OMP_THEME_PREFERENCE";

    public const string SystemMode = "system";
    public const string LightMode = "light";
    public const string DarkMode = "dark";

    public static string ReadMode(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ParseMode(request.Cookies[CookieName]);
    }

    public static string ParseMode(string? cookieValue)
    {
        if (string.IsNullOrWhiteSpace(cookieValue))
        {
            return SystemMode;
        }

        string json;
        try
        {
            json = Uri.UnescapeDataString(cookieValue);
        }
        catch (UriFormatException)
        {
            return SystemMode;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return SystemMode;
            }

            if (!root.TryGetProperty("version", out var version) ||
                version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out var versionNumber) ||
                versionNumber != 1)
            {
                return SystemMode;
            }

            if (!root.TryGetProperty("mode", out var mode) ||
                mode.ValueKind != JsonValueKind.String)
            {
                return SystemMode;
            }

            return mode.GetString() switch
            {
                LightMode => LightMode,
                DarkMode => DarkMode,
                SystemMode => SystemMode,
                _ => SystemMode
            };
        }
        catch (JsonException)
        {
            return SystemMode;
        }
    }
}
