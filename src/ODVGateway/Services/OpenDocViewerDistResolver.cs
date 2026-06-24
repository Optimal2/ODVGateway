using Microsoft.Extensions.Options;
using ODVGateway.Options;

namespace ODVGateway.Services;

public sealed class OpenDocViewerDistResolver
{
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<OpenDocViewerDistResolver> _logger;
    private readonly IOptionsMonitor<ODVGatewayOptions> _options;

    public OpenDocViewerDistResolver(
        IWebHostEnvironment environment,
        ILogger<OpenDocViewerDistResolver> logger,
        IOptionsMonitor<ODVGatewayOptions> options)
    {
        _environment = environment;
        _logger = logger;
        _options = options;
    }

    public string? ResolveDistPath()
    {
        var options = _options.CurrentValue;
        var configuredPath = options.OpenDocViewerDistPath;
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var resolved = ResolvePath(configuredPath);
            if (IsOpenDocViewerDist(resolved))
            {
                return resolved;
            }

            _logger.LogError(
                "Configured OpenDocViewer dist path is not a readable dist folder. Path={OpenDocViewerDistPath}",
                resolved);
        }

        if (options.RequireExplicitOpenDocViewerDistPath)
        {
            _logger.LogError(
                "ODVGateway requires an explicit OpenDocViewer dist path, but no valid configured path was found.");
            return null;
        }

        var candidates = new[]
        {
            ResolveRelativePath("wwwroot", "odv"),
            ResolveRelativePath("wwwroot", "OpenDocViewer"),
            ResolveRelativePath("..", "OpenDocViewer"),
            ResolveRelativePath("..", "OpenDocViewer", "dist"),
            ResolveRelativePath("..", "..", "..", "OpenDocViewer", "dist")
        };

        var fallback = candidates.Where(IsOpenDocViewerDist).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(fallback))
        {
            _logger.LogWarning(
                "ODVGateway resolved OpenDocViewer dist path through fallback probing. Set OpenDocViewerDistPath explicitly in production. Path={OpenDocViewerDistPath}",
                fallback);
        }

        return fallback;
    }

    private string ResolvePath(string path)
    {
        return Path.GetFullPath(
            Path.IsPathRooted(path)
                ? path
                : Path.Join(_environment.ContentRootPath, path));
    }

    private string ResolveRelativePath(params string[] relativeSegments)
    {
        var segments = new string[relativeSegments.Length + 1];
        segments[0] = _environment.ContentRootPath;
        Array.Copy(relativeSegments, 0, segments, 1, relativeSegments.Length);
        return Path.GetFullPath(Path.Join(segments));
    }

    private static bool IsOpenDocViewerDist(string path)
    {
        return Directory.Exists(path) &&
            File.Exists(Path.Join(path, "index.html"));
    }
}
