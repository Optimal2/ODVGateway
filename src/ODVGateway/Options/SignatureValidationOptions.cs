using System.Security.Cryptography.X509Certificates;

namespace ODVGateway.Options;

/// <summary>
/// Server-side PDF signature validation (GET /signatures/{sessionKey}/{fileIndex}).
/// Off by default: the endpoint answers 404 until <see cref="Enabled"/> is set, because validation
/// needs a trust anchor surface and, in Online revocation mode, bounded outbound HTTP to issuer CRL
/// endpoints. See docs/PDF-SIGNATURE-VALIDATION.md.
/// </summary>
public sealed class SignatureValidationOptions
{
    /// <summary>Master switch. False keeps GET /signatures/... a 404 with a clear error body.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Seed the custom trust store with the Windows trusted-root stores (LocalMachine and
    /// CurrentUser). Set false to validate against configured anchors only.
    /// </summary>
    public bool UseWindowsTrustedRoots { get; set; } = true;

    /// <summary>
    /// Optional directory with extra trust anchors (.cer/.crt/.der/.pem). Relative paths resolve
    /// against the content root, the same way trustedSourceRoots do. The directory is read once per
    /// gateway process: restart after changing it.
    /// </summary>
    public string? ExtraAnchorsDirectory { get; set; }

    /// <summary>
    /// Optional directory with offline CRL files (.crl/.der/.pem). When it holds a fresh CRL signed
    /// by a chain issuer for every non-anchor element, revocation is decided from those files and no
    /// outbound HTTP is needed for that signature.
    /// </summary>
    public string? CrlDirectory { get; set; }

    /// <summary>
    /// Revocation handling, configured as the string "Online" (default), "Offline" or "NoCheck".
    /// </summary>
    public SignatureRevocationMode RevocationMode { get; set; } = SignatureRevocationMode.Online;

    /// <summary>
    /// How long one Online CRL fetch may take, in seconds (clamped to 1–30), including DNS,
    /// connection and body reading. Also bounded by the remaining file budget.
    /// </summary>
    public int RevocationTimeoutSeconds { get; set; } = 15;

    /// <summary>Optional exact DNS host allow-list, applied in addition to public-address checks.</summary>
    public string[] RevocationHostAllowList { get; set; } = [];

    /// <summary>
    /// Largest PDF that is validated. Zero (the default) follows the gateway's existing source
    /// transport limit (maxSourcePackFrameBytes); a positive value caps the endpoint separately,
    /// but never above what the source transport can carry.
    /// </summary>
    public long MaxFileBytes { get; set; }

    /// <summary>Maps the configured mode to its legacy enum representation; not used for native chain policy.</summary>
    public X509RevocationMode GetRevocationMode() => RevocationMode switch
    {
        SignatureRevocationMode.Offline => X509RevocationMode.Offline,
        SignatureRevocationMode.NoCheck => X509RevocationMode.NoCheck,
        _ => X509RevocationMode.Online
    };
}

/// <summary>
/// Revocation handling for signature chain building. Online and Offline never produce a verdict of
/// "valid" for a signature whose revocation could not be established. "NoCheck" always reports
/// "unknown" with the trust reason "revocation-not-checked".
/// </summary>
public enum SignatureRevocationMode
{
    /// <summary>Fetch issuer CRLs through the bounded public-address HTTP transport. Unresolved evidence is unknown.</summary>
    Online = 0,

    /// <summary>Use configured offline CRLs only; never reach out. Nothing proven is reported as unknown.</summary>
    Offline = 1,

    /// <summary>Skip revocation. Reported as unknown with trustReason "revocation-not-checked", never as a clean signature.</summary>
    NoCheck = 2
}
