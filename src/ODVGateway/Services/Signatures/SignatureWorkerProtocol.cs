using System.Text.Json.Serialization;
using ODVGateway.Models;

namespace ODVGateway.Services.Signatures;

/// <summary>
/// The parent/worker contract for isolated signature validation. The gateway starts the same
/// assembly with <see cref="WorkerArgument"/>, writes the document bytes to the worker's stdin,
/// and reads one JSON <see cref="WorkerEnvelope"/> from its stdout. Nothing is written to disk on
/// either side: no temp files, no persisted bytes. Stderr carries diagnostics for humans only and
/// is drained and discarded by the parent.
/// </summary>
internal static class SignatureWorkerProtocol
{
    /// <summary>Entry-mode argument: run one validation and exit instead of serving HTTP.</summary>
    public const string WorkerArgument = "--signature-worker";

    /// <summary>Env var that must be "1" for the worker to honor any test mode.</summary>
    public const string TestEnvironmentVariable = "ODVGATEWAY_SIGNATURE_WORKER_TEST";

    /// <summary>Largest worker stdout the parent accepts before killing the child as crashed.</summary>
    public const int MaxStdoutBytes = 32 * 1024 * 1024;

    /// <summary>Largest stdin the worker reads; the parent already enforces the file limit.</summary>
    public const int MaxStdinBytes = 256 * 1024 * 1024;

    /// <summary>How often the parent samples the worker's working set.</summary>
    public static readonly TimeSpan MemoryPollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>Bare flag (no value): loop over length-prefixed requests instead of exiting after one.</summary>
    public const string PersistentArgument = "--persistent";

    /// <summary>Largest per-request header the persistent worker accepts.</summary>
    public const int MaxHeaderBytes = 1024 * 1024;

    /// <summary>Most requests one pooled worker serves before it is retired.</summary>
    public const int MaxRequestsPerWorker = 25;

    /// <summary>Most idle workers the pool retains; extras are retired.</summary>
    public const int MaxIdleWorkers = 2;

    /// <summary>Idle workers older than this are retired on next checkout.</summary>
    public static readonly TimeSpan IdleWorkerExpiry = TimeSpan.FromMinutes(5);

    /// <summary>Worker argument names. Values travel through ProcessStartInfo.ArgumentList.</summary>
    public static class Arguments
    {
        public const string RevocationMode = "--revocation-mode";
        public const string RevocationTimeoutSeconds = "--revocation-timeout-seconds";
        public const string RevocationHostAllowList = "--revocation-host-allow-list";
        public const string UseWindowsRoots = "--use-windows-roots";
        public const string AnchorsDirectory = "--anchors-dir";
        public const string CrlDirectory = "--crl-dir";
        public const string ContentRoot = "--content-root";
        public const string GatewayHost = "--gateway-host";
        public const string TestMode = "--worker-test-mode";
    }

    /// <summary>Test-only worker behaviors, honored only with <see cref="TestEnvironmentVariable"/>.</summary>
    public static class TestModes
    {
        /// <summary>Drain stdin, then sleep until killed. Proves the wall-clock timeout.</summary>
        public const string Hang = "hang";

        /// <summary>Drain stdin, then allocate until killed. Proves the memory cap.</summary>
        public const string Allocate = "allocate";

        /// <summary>Drain stdin, then print garbage to stdout and exit 0. Proves malformed-output handling.</summary>
        public const string BadOutput = "bad-output";

        /// <summary>Validate normally but skip the page-tree pre-check. Proves isolation: a cyclic
        /// page tree then overflows the worker's stack instead of the gateway's.</summary>
        public const string BypassPrecheck = "bypass-precheck";
    }

    /// <summary>Envelope statuses the worker reports on stdout.</summary>
    public static class Statuses
    {
        public const string Ok = "ok";
        public const string Encrypted = "encrypted";
        public const string FormatError = "format-error";
        public const string Error = "error";
    }
}

/// <summary>
/// One worker answer. <c>Response</c> is set only for <c>ok</c>; <c>Error</c> carries a fixed
/// message (encrypted/format-error, reconstructed into typed exceptions by the parent and never
/// shown to HTTP clients) or an exception type name (error). No document content, no stack trace.
/// </summary>
internal sealed record WorkerEnvelope(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("response")] PdfSignatureValidationResponse? Response,
    [property: JsonPropertyName("error")] string? Error);
