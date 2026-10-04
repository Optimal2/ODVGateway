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

    /// <summary>Concurrent signature requests, including buffering. Clamped to 1–16; no queue.</summary>
    public int MaxConcurrentValidations { get; set; } = 2;

    /// <summary>
    /// Run the PDF open, signature extraction and validation of each file in a separate worker
    /// process (the same assembly started with <c>--signature-worker</c>, fed the document bytes
    /// over stdin). A worker that crashes, times out or exceeds its memory cap costs one request
    /// (a named HTTP 503) instead of the gateway process. False keeps the in-process path, which
    /// is only safe for PDFs from a trusted archive: a crafted file can overflow the stack inside
    /// the PDF library's eager open, and a stack overflow terminates the whole process.
    /// </summary>
    public bool IsolateProcess { get; set; } = true;

    /// <summary>
    /// Wall-clock budget for one worker run, in seconds (clamped to 1-120). A worker that has not
    /// answered in time is killed and the request fails with
    /// <c>validation-worker-timeout</c> (HTTP 503).
    /// </summary>
    public int WorkerTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Working-set cap for one worker process, in bytes (clamped to 32 MiB-8 GiB). A worker that
    /// grows past it is killed and the request fails with <c>validation-worker-memory</c>
    /// (HTTP 503).
    /// </summary>
    public long WorkerMaxMemoryBytes { get; set; } = 512L * 1024L * 1024L;

    /// <summary>
    /// Test-only worker fault injection (<c>hang</c>, <c>allocate</c>, <c>bad-output</c>,
    /// <c>bypass-precheck</c>). Honored only when the
    /// <c>ODVGATEWAY_SIGNATURE_WORKER_TEST</c> environment variable is <c>1</c>; any other value
    /// (or an unset variable) makes the worker refuse to start. Never set in production.
    /// </summary>
    public string? WorkerTestMode { get; set; }

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
