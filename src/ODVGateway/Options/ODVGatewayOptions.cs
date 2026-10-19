namespace ODVGateway.Options;

public sealed class ODVGatewayOptions
{
    public const string SectionName = "ODVGateway";

    public string OpenDocViewerDistPath { get; set; } = string.Empty;

    public bool RequireExplicitOpenDocViewerDistPath { get; set; }

    public int SessionTtlMinutes { get; set; } = 30;

    public int MaxConcurrentSessions { get; set; } = 50000;

    public long MaxPrepBodyBytes { get; set; } = 50L * 1024L * 1024L;

    public long MaxSourcePackFrameBytes { get; set; } = 64L * 1024L * 1024L;

    public long MaxSourceProxyBytes { get; set; }

    public int SourcePackStreamBufferBytes { get; set; } = 128 * 1024;

    public bool ExposeOpenDocViewerDistPathInHealth { get; set; }

    public bool TrustClientFilePath { get; set; }

    public string[] TrustedSourceRoots { get; set; } = [];

    public bool AllowOpenDocViewerFallbackWithoutSession { get; set; }

    // Keep enabled in WebClient deployments: with it disabled the viewer page carries no
    // ?bundleUrl=, OpenDocViewer's bootstrap then prefers the same-origin parent payload,
    // and the prepared gateway session/source-pack transport is bypassed. See
    // GetUseBundleUrlHandoffWarning and the README "Source transport" section.
    public bool UseBundleUrlHandoff { get; set; } = true;

    /// <summary>
    /// The startup warning for a disabled bundle-URL handoff, or null when the handoff is
    /// enabled. Kept on the options type so the exact wording is unit-testable without
    /// booting the host; Program logs the returned text at startup.
    /// </summary>
    public string? GetUseBundleUrlHandoffWarning()
    {
        if (UseBundleUrlHandoff)
        {
            return null;
        }

        var warning =
            "ODVGateway is configured with useBundleUrlHandoff disabled. Without the ?bundleUrl= " +
            "redirect, OpenDocViewer's bootstrap prefers the same-origin parent (WebClient) payload " +
            "over the gateway's inline bundle, so the viewer can bypass the prepared gateway session " +
            "and its source-pack transport and fetch sources directly from WebClient instead. " +
            "Keep useBundleUrlHandoff enabled for WebClient iframe deployments.";

        if (WebClientHandoff.AllowedInitiatorUrls.Any(url => !string.IsNullOrWhiteSpace(url)))
        {
            warning +=
                " webClientHandoff.allowedInitiatorUrls is set, so this deployment is a WebClient " +
                "handoff deployment and is affected by that bypass.";
        }

        return warning;
    }

    public string SourceCacheControl { get; set; } = "no-store";

    public string? ContentSecurityPolicy { get; set; }

    public InlineSourceOptions InlineSources { get; set; } = new();

    public RemoteInlineSourceOptions RemoteInlineSources { get; set; } = new();

    public WebClientSourceFallbackOptions WebClientSourceFallback { get; set; } = new();

    public WebClientHandoffOptions WebClientHandoff { get; set; } = new();

    public SignatureValidationOptions Signatures { get; set; } = new();

    public Dictionary<string, MetadataAliasOption> MetadataAliases { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> ContentTypes { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class WebClientSourceFallbackOptions
{
    public bool Enabled { get; set; } = true;

    public bool RequireSameHost { get; set; } = true;

    public string[] AllowedHosts { get; set; } = [];

    public bool UseFilePathUrlWhenDirectFileMissing { get; set; } = true;

    public bool UseWhenDirectFileMissing { get; set; } = true;

    public string UrlTemplate { get; set; } = "/WebClientODV/DocumentView/GetStream/?ticket={fileId}";

    public bool ProxyThroughGateway { get; set; } = true;

    public int ProxyThroughGatewayAboveSourceCount { get; set; } = 1000;

    public int ProxyMaxConcurrency { get; set; } = 14;
}

public sealed class WebClientHandoffOptions
{
    public string[] AllowedInitiatorUrls { get; set; } = [];

    public bool AllowMissingInitiatorHeaders { get; set; }
}

public sealed class InlineSourceOptions
{
    public bool Enabled { get; set; } = true;

    public long MaxTotalBytes { get; set; } = 16L * 1024L * 1024L;

    public long MaxFileBytes { get; set; } = 2L * 1024L * 1024L;

    public string[] Extensions { get; set; } =
    [
        "tif",
        "tiff",
        "jpg",
        "jpeg",
        "png",
        "bmp",
        "gif",
        "webp"
    ];
}

public sealed class RemoteInlineSourceOptions
{
    public bool Enabled { get; set; } = true;

    public bool RequireSameHost { get; set; } = true;

    // Zero intentionally means "no count cap"; MaxTotalBytes and MaxFileBytes still bound memory use.
    public int MaxCount { get; set; }

    public long MaxTotalBytes { get; set; } = 256L * 1024L * 1024L;

    public long MaxFileBytes { get; set; } = 2L * 1024L * 1024L;

    public int MaxConcurrency { get; set; } = 2;

    public int RequestTimeoutMs { get; set; } = 15000;

    public int RetryCount { get; set; } = 2;

    public int RetryBaseDelayMs { get; set; } = 150;

    public string[] AspxAuthCookieNames { get; set; } =
    [
        ".ASPXAUTH",
        "ASPXAUTH"
    ];

    public string[] SessionCookieNames { get; set; } =
    [
        "ASP.NET_SessionId"
    ];

    public string[] Extensions { get; set; } =
    [
        "tif",
        "tiff",
        "jpg",
        "jpeg",
        "png",
        "bmp",
        "gif",
        "webp"
    ];
}

public sealed class MetadataAliasOption
{
    public string? FieldId { get; set; }

    public string[] FieldIds { get; set; } = [];

    public string Prefer { get; set; } = "valueThenLookup";

    public string? Label { get; set; }

    public string? Type { get; set; }

    public string[] Contexts { get; set; } = [];

    public IReadOnlyList<string> GetFieldIds()
    {
        return new[] { FieldId }
            .Concat(FieldIds)
            .Where(fieldId => !string.IsNullOrWhiteSpace(fieldId))
            .Select(fieldId => fieldId!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
