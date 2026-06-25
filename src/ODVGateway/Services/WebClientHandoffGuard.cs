using Microsoft.Extensions.Primitives;
using Microsoft.Extensions.Options;
using ODVGateway.Options;

namespace ODVGateway.Services;

public sealed class WebClientHandoffGuard
{
    private readonly IOptionsMonitor<ODVGatewayOptions> _options;
    private long _rejectedCount;

    public WebClientHandoffGuard(IOptionsMonitor<ODVGatewayOptions> options)
    {
        _options = options;
    }

    public long RejectedCount => Interlocked.Read(ref _rejectedCount);

    public HandoffGuardResult Validate(HttpRequest request)
    {
        var options = _options.CurrentValue.WebClientHandoff;
        var allowedInitiators = NormalizeAllowedInitiators(request, options.AllowedInitiatorUrls);
        if (allowedInitiators.Count == 0)
        {
            return HandoffGuardResult.Allowed();
        }

        if (TryMatchHeader(request.Headers.Referer, allowedInitiators, includePath: true, out var referer))
        {
            return HandoffGuardResult.Allowed(referer);
        }

        if (TryMatchHeader(request.Headers.Origin, allowedInitiators, includePath: false, out var origin))
        {
            return HandoffGuardResult.Allowed(origin);
        }

        var hasInitiatorHeader =
            !StringValues.IsNullOrEmpty(request.Headers.Referer) ||
            !StringValues.IsNullOrEmpty(request.Headers.Origin);
        if (!hasInitiatorHeader && options.AllowMissingInitiatorHeaders)
        {
            return HandoffGuardResult.Allowed();
        }

        return Reject(
            hasInitiatorHeader
                ? "The request initiator is not allowed for ODVGateway handoff."
                : "The request did not include Origin or Referer headers for ODVGateway handoff.");
    }

    private HandoffGuardResult Reject(string reason)
    {
        Interlocked.Increment(ref _rejectedCount);
        return HandoffGuardResult.Rejected(reason);
    }

    private static bool TryMatchHeader(
        StringValues headerValues,
        IReadOnlyList<AllowedInitiator> allowedInitiators,
        bool includePath,
        out string? safeInitiator)
    {
        safeInitiator = null;
        foreach (var rawValue in headerValues)
        {
            if (!Uri.TryCreate(rawValue, UriKind.Absolute, out var initiator))
            {
                continue;
            }

            safeInitiator = ToSafeInitiatorText(initiator);
            if (allowedInitiators.Any(allowed => IsAllowedInitiator(initiator, allowed, includePath)))
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<AllowedInitiator> NormalizeAllowedInitiators(
        HttpRequest request,
        IEnumerable<string>? configuredUrls)
    {
        return (configuredUrls ?? [])
            .Select(configuredUrl => NormalizeAllowedInitiator(request, configuredUrl))
            .Where(initiator => initiator is not null)
            .Select(initiator => initiator!)
            .Distinct()
            .ToArray();
    }

    private static AllowedInitiator? NormalizeAllowedInitiator(HttpRequest request, string? configuredUrl)
    {
        var trimmed = configuredUrl?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absolute) && IsHttpUri(absolute))
        {
            return AllowedInitiator.FromUri(absolute);
        }

        if (trimmed.StartsWith("/", StringComparison.Ordinal))
        {
            return Uri.TryCreate($"{request.Scheme}://{request.Host}{trimmed}", UriKind.Absolute, out var relativeToGateway)
                ? AllowedInitiator.FromUri(relativeToGateway)
                : null;
        }

        return Uri.TryCreate($"{request.Scheme}://{trimmed}", UriKind.Absolute, out var hostOnly) && IsHttpUri(hostOnly)
            ? AllowedInitiator.FromUri(hostOnly)
            : null;
    }

    private static bool IsAllowedInitiator(Uri initiator, AllowedInitiator allowed, bool includePath)
    {
        if (!IsHttpUri(initiator))
        {
            return false;
        }

        if (!allowed.Scheme.Equals(initiator.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !allowed.Host.Equals(initiator.Host, StringComparison.OrdinalIgnoreCase) ||
            allowed.Port != initiator.Port)
        {
            return false;
        }

        if (!allowed.RequiresPathMatch)
        {
            return true;
        }

        return includePath && PathMatches(initiator.AbsolutePath, allowed.PathPrefix);
    }

    private static bool PathMatches(string candidatePath, string allowedPath)
    {
        if (allowedPath.EndsWith("/", StringComparison.Ordinal))
        {
            return candidatePath.StartsWith(allowedPath, StringComparison.OrdinalIgnoreCase);
        }

        return candidatePath.Equals(allowedPath, StringComparison.OrdinalIgnoreCase) ||
            candidatePath.StartsWith(allowedPath + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHttpUri(Uri uri)
    {
        return uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
            uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
    }

    private static string ToSafeInitiatorText(Uri initiator)
    {
        return $"{initiator.Scheme}://{initiator.Authority}{initiator.AbsolutePath}";
    }
}

public sealed record HandoffGuardResult(bool IsAllowed, string? Reason, string? Initiator)
{
    public static HandoffGuardResult Allowed(string? initiator = null)
    {
        return new HandoffGuardResult(true, null, initiator);
    }

    public static HandoffGuardResult Rejected(string reason)
    {
        return new HandoffGuardResult(false, reason, null);
    }
}

internal sealed record AllowedInitiator(
    string Scheme,
    string Host,
    int Port,
    string PathPrefix,
    bool RequiresPathMatch)
{
    public static AllowedInitiator FromUri(Uri uri)
    {
        var path = uri.AbsolutePath;
        var requiresPathMatch = !string.IsNullOrWhiteSpace(path) && path != "/";
        return new AllowedInitiator(
            uri.Scheme,
            uri.Host,
            uri.Port,
            requiresPathMatch ? path : "/",
            requiresPathMatch);
    }
}
