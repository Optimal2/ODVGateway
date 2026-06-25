using Microsoft.Extensions.Options;
using ODVGateway.Models;
using ODVGateway.Options;

namespace ODVGateway.Services;

public sealed class DirectSourceFileResolver
{
    private readonly IOptionsMonitor<ODVGatewayOptions> _options;
    private readonly ILogger<DirectSourceFileResolver> _logger;

    public DirectSourceFileResolver(
        IOptionsMonitor<ODVGatewayOptions> options,
        ILogger<DirectSourceFileResolver> logger)
    {
        _options = options;
        _logger = logger;
    }

    public bool TryResolve(GatewaySourceFile source, out DirectSourceFile directSource)
    {
        return TryResolve(source.FilePath, out directSource);
    }

    public bool TryResolve(string? sourcePath, out DirectSourceFile directSource)
    {
        directSource = null!;

        var options = _options.CurrentValue;
        if (!options.TrustClientFilePath)
        {
            return false;
        }

        if (!TryNormalizeSourcePath(sourcePath, out var candidatePath))
        {
            return false;
        }

        var invalidTrustedRoots = GetInvalidTrustedRoots(options.TrustedSourceRoots);
        if (invalidTrustedRoots.Count > 0)
        {
            _logger.LogError(
                "Rejected client file path because TrustedSourceRoots contains non-absolute entries while TrustClientFilePath is enabled. InvalidRootCount={InvalidRootCount}",
                invalidTrustedRoots.Count);
            return false;
        }

        var trustedRoots = NormalizeTrustedRoots(options.TrustedSourceRoots);
        if (trustedRoots.Count == 0)
        {
            _logger.LogWarning(
                "Rejected client file path because TrustClientFilePath is enabled without any TrustedSourceRoots.");
            return false;
        }

        if (!IsUnderTrustedRoot(candidatePath, trustedRoots))
        {
            _logger.LogWarning(
                "Rejected client file path outside configured TrustedSourceRoots.");
            return false;
        }

        try
        {
            var info = new FileInfo(candidatePath);
            if (!info.Exists)
            {
                return false;
            }

            directSource = new DirectSourceFile(info.FullName, Math.Max(0, info.Length));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public FileStream OpenRead(DirectSourceFile directSource)
    {
        return new FileStream(
            directSource.FullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 128 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    private static bool TryNormalizeSourcePath(string? sourcePath, out string fullPath)
    {
        fullPath = string.Empty;

        var raw = sourcePath?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        if (Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            if (!uri.IsFile)
            {
                return false;
            }

            raw = uri.LocalPath;
        }

        try
        {
            fullPath = Path.GetFullPath(raw);
            return Path.IsPathFullyQualified(fullPath);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static IReadOnlyList<string> GetInvalidTrustedRoots(IEnumerable<string>? roots)
    {
        return (roots ?? [])
            .Select(root => root?.Trim())
            .Where(trimmedRoot => !string.IsNullOrWhiteSpace(trimmedRoot))
            .Where(trimmedRoot => !TryNormalizeTrustedRoot(trimmedRoot, out _))
            .Select(trimmedRoot => trimmedRoot!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static IReadOnlyList<string> NormalizeTrustedRoots(IEnumerable<string>? roots)
    {
        return (roots ?? [])
            .Select(root => TryNormalizeTrustedRoot(root, out var normalizedRoot) ? normalizedRoot : null)
            .Where(normalizedRoot => !string.IsNullOrWhiteSpace(normalizedRoot))
            .Select(normalizedRoot => normalizedRoot!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool TryNormalizeTrustedRoot(string? root, out string normalizedRoot)
    {
        normalizedRoot = string.Empty;

        var raw = root?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        if (Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            if (!uri.IsFile)
            {
                return false;
            }

            raw = uri.LocalPath;
        }

        if (!Path.IsPathFullyQualified(raw))
        {
            return false;
        }

        try
        {
            normalizedRoot = TrimTrailingDirectorySeparators(Path.GetFullPath(raw));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsUnderTrustedRoot(string candidatePath, IReadOnlyList<string> trustedRoots)
    {
        foreach (var root in trustedRoots)
        {
            if (candidatePath.Equals(root, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var relativePath = Path.GetRelativePath(root, candidatePath);
            if (relativePath.Length == 0)
            {
                return true;
            }

            if (!relativePath.Equals("..", StringComparison.Ordinal) &&
                !relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                !relativePath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal) &&
                !Path.IsPathRooted(relativePath))
            {
                return true;
            }
        }

        return false;
    }

    private static string TrimTrailingDirectorySeparators(string path)
    {
        var root = Path.GetPathRoot(path);
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.IsNullOrEmpty(trimmed) || trimmed.Equals(root, StringComparison.OrdinalIgnoreCase)
            ? path
            : trimmed;
    }
}

public sealed record DirectSourceFile(string FullPath, long Length);
