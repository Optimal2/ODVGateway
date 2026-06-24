using ODVGateway.Models;
using ODVGateway.Options;

namespace ODVGateway.Services;

public enum WebClientFallbackUrlKind
{
    None,
    FilePath,
    Template
}

public sealed record WebClientFallbackUrlResult(string? Url, WebClientFallbackUrlKind Kind)
{
    public static readonly WebClientFallbackUrlResult None = new(null, WebClientFallbackUrlKind.None);
}

public sealed class WebClientFallbackUrlBuilder
{
    public WebClientFallbackUrlResult BuildFallbackUrl(
        HttpRequest request,
        string sessionKey,
        GatewaySourceFile file,
        WebClientSourceFallbackOptions options)
    {
        if (!options.Enabled || !options.UseWhenDirectFileMissing)
        {
            return WebClientFallbackUrlResult.None;
        }

        if (options.UseFilePathUrlWhenDirectFileMissing)
        {
            var filePathUrl = BuildBrowserUrlFromSourcePath(request, file.FilePath, options);
            if (filePathUrl is not null)
            {
                return new WebClientFallbackUrlResult(filePathUrl, WebClientFallbackUrlKind.FilePath);
            }
        }

        if (string.IsNullOrWhiteSpace(options.UrlTemplate) || string.IsNullOrWhiteSpace(file.FileId))
        {
            return WebClientFallbackUrlResult.None;
        }

        var url = options.UrlTemplate
            .Replace("{fileId}", Uri.EscapeDataString(file.FileId), StringComparison.OrdinalIgnoreCase)
            .Replace("{sessionKey}", Uri.EscapeDataString(sessionKey), StringComparison.OrdinalIgnoreCase)
            .Replace("{fileIndex}", file.Index.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{extension}", Uri.EscapeDataString(file.Extension ?? string.Empty), StringComparison.OrdinalIgnoreCase);

        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute) &&
            (absolute.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
             absolute.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            return IsAllowedWebClientFallbackUrl(request, absolute, options)
                ? new WebClientFallbackUrlResult(absolute.ToString(), WebClientFallbackUrlKind.Template)
                : WebClientFallbackUrlResult.None;
        }

        if (url.StartsWith("/", StringComparison.Ordinal))
        {
            return new WebClientFallbackUrlResult(
                $"{request.Scheme}://{request.Host}{url}",
                WebClientFallbackUrlKind.Template);
        }

        var basePath = request.PathBase.HasValue ? request.PathBase.Value : string.Empty;
        return new WebClientFallbackUrlResult(
            $"{request.Scheme}://{request.Host}{basePath}/{url.TrimStart('/')}",
            WebClientFallbackUrlKind.Template);
    }

    private static string? BuildBrowserUrlFromSourcePath(
        HttpRequest request,
        string? sourcePath,
        WebClientSourceFallbackOptions options)
    {
        var raw = sourcePath?.Trim();
        if (string.IsNullOrWhiteSpace(raw)) return null;

        if (Uri.TryCreate(raw, UriKind.Absolute, out var absolute))
        {
            if (absolute.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                absolute.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return IsAllowedWebClientFallbackUrl(request, absolute, options)
                    ? absolute.ToString()
                    : null;
            }

            return null;
        }

        if (raw.StartsWith("//", StringComparison.Ordinal))
        {
            var schemeRelative = $"{request.Scheme}:{raw}";
            return Uri.TryCreate(schemeRelative, UriKind.Absolute, out var schemeRelativeUri) &&
                IsAllowedWebClientFallbackUrl(request, schemeRelativeUri, options)
                ? schemeRelativeUri.ToString()
                : null;
        }

        if (raw.StartsWith("/", StringComparison.Ordinal))
        {
            return $"{request.Scheme}://{request.Host}{raw}";
        }

        if (LooksLikeLocalFilePath(raw)) return null;

        return $"{request.Scheme}://{request.Host}/{raw.TrimStart('/')}";
    }

    private static bool IsAllowedWebClientFallbackUrl(
        HttpRequest request,
        Uri absolute,
        WebClientSourceFallbackOptions options)
    {
        if (!absolute.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !absolute.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var allowedHosts = options.AllowedHosts
            .Where(host => !string.IsNullOrWhiteSpace(host))
            .ToArray();
        if (allowedHosts.Any(host => HostMatches(host, absolute)))
        {
            return true;
        }

        return !options.RequireSameHost || HostMatches(request.Host.ToString(), absolute);
    }

    private static bool HostMatches(string configuredHost, Uri absolute)
    {
        var normalizedHost = configuredHost.Trim();
        if (string.IsNullOrWhiteSpace(normalizedHost))
        {
            return false;
        }

        if (Uri.TryCreate(normalizedHost, UriKind.Absolute, out var configuredUri))
        {
            return HostAndPortMatch(
                configuredUri.Host,
                configuredUri.IsDefaultPort ? null : configuredUri.Port,
                absolute);
        }

        var configuredPort = (int?)null;
        var portSeparatorIndex = normalizedHost.LastIndexOf(':');
        if (portSeparatorIndex > 0 &&
            int.TryParse(normalizedHost[(portSeparatorIndex + 1)..], out var parsedPort))
        {
            configuredPort = parsedPort;
            normalizedHost = normalizedHost[..portSeparatorIndex];
        }

        return HostAndPortMatch(normalizedHost, configuredPort, absolute);
    }

    private static bool HostAndPortMatch(string configuredHost, int? configuredPort, Uri absolute)
    {
        if (!configuredHost.Equals(absolute.Host, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return configuredPort is null || configuredPort.Value == absolute.Port;
    }

    private static bool LooksLikeLocalFilePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (value.StartsWith(@"\\", StringComparison.Ordinal)) return true;
        if (value.Length >= 3 &&
            char.IsLetter(value[0]) &&
            value[1] == ':' &&
            (value[2] == '\\' || value[2] == '/'))
        {
            return true;
        }

        return value.Contains('\\', StringComparison.Ordinal);
    }
}
