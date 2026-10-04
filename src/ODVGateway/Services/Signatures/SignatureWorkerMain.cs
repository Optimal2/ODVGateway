using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ODVGateway.Models;
using ODVGateway.Options;

namespace ODVGateway.Services.Signatures;

/// <summary>
/// The isolated validation worker: the same assembly started with
/// <c>--signature-worker</c>. Reads the document bytes from stdin, runs the exact same open,
/// extraction and validation as the in-process path, and writes one JSON
/// <see cref="WorkerEnvelope"/> to stdout. Stdout carries nothing else, ever: diagnostics go to
/// stderr. Exit code 0 means an envelope was written (including encrypted, format-error and error
/// envelopes); any other exit means the parent must report
/// <c>validation-worker-crashed</c>. This entry point never builds the web host, never touches
/// logging providers, and never writes to disk.
/// </summary>
internal static class SignatureWorkerMain
{
    /// <summary>True when the process was started as a validation worker.</summary>
    public static bool ShouldRun(string[] args) =>
        args.Any(arg => string.Equals(arg, SignatureWorkerProtocol.WorkerArgument, StringComparison.Ordinal));

    /// <summary>Runs one validation; the return value is the process exit code.</summary>
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Any(arg => string.Equals(arg, SignatureWorkerProtocol.PersistentArgument, StringComparison.Ordinal)))
        {
            return await RunPersistentAsync();
        }

        WorkerConfig? config;
        try
        {
            config = WorkerConfig.Parse(args);
        }
        catch (ArgumentException exception)
        {
            await Console.Error.WriteLineAsync("signature worker: " + exception.Message);
            return 2;
        }

        if (!TestModeAllowed(config.TestMode))
        {
            await Console.Error.WriteLineAsync("signature worker: test modes need ODVGATEWAY_SIGNATURE_WORKER_TEST=1.");
            return 2;
        }

        try
        {
            switch (config.TestMode)
            {
                case SignatureWorkerProtocol.TestModes.Hang:
                    await DrainStdinAsync();
                    await Task.Delay(Timeout.InfiniteTimeSpan);
                    return 0;
                case SignatureWorkerProtocol.TestModes.Allocate:
                    await DrainStdinAsync();
                    AllocateUntilKilled();
                    return 0;
                case SignatureWorkerProtocol.TestModes.BadOutput:
                    await DrainStdinAsync();
                    await WriteStdoutAsync("this is not a validation envelope {{{"u8.ToArray());
                    return 0;
                case SignatureWorkerProtocol.TestModes.BypassPrecheck:
                    PdfPageTreePrecheck.DisabledForTesting = true;
                    break;
                case not null:
                    await Console.Error.WriteLineAsync("signature worker: unknown test mode.");
                    return 2;
            }

            var document = await ReadStdinAsync();
            if (document.Length == 0)
            {
                await WriteEnvelopeAsync(new WorkerEnvelope(SignatureWorkerProtocol.Statuses.FormatError, null,
                    "The worker received an empty document."));
                return 0;
            }

            await WriteEnvelopeAsync(await ProcessOneAsync(config, document));
            return 0;
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        {
            await Console.Error.WriteLineAsync("signature worker: stream failure: " + exception.GetType().Name);
            return 1;
        }
    }

    /// <summary>
    /// Serves the parent's warm pool: loops over length-prefixed requests
    /// ([4-byte header length][header JSON][8-byte body length][body]) and answers each with an
    /// [8-byte length][envelope JSON] frame. Stdin EOF retires the worker with exit 0; any framing
    /// error exits 1. Per-request configuration arrives in the header, so a pooled worker never
    /// depends on argv.
    /// </summary>
    private static async Task<int> RunPersistentAsync()
    {
        var stdin = Console.OpenStandardInput();
        var stdout = Console.OpenStandardOutput();
        while (true)
        {
            var headerLengthBytes = await ReadExactAsync(stdin, sizeof(int));
            if (headerLengthBytes is null)
            {
                return 0;
            }

            var headerLength = BitConverter.ToInt32(headerLengthBytes);
            if (headerLength <= 0 || headerLength > SignatureWorkerProtocol.MaxHeaderBytes)
            {
                return 1;
            }

            var header = await ReadExactAsync(stdin, headerLength);
            var bodyLengthBytes = header is null ? null : await ReadExactAsync(stdin, sizeof(long));
            if (header is null || bodyLengthBytes is null)
            {
                return 1;
            }

            var bodyLength = BitConverter.ToInt64(bodyLengthBytes);
            if (bodyLength < 0 || bodyLength > SignatureWorkerProtocol.MaxStdinBytes)
            {
                return 1;
            }

            var body = await ReadExactAsync(stdin, (int)bodyLength);
            if (body is null)
            {
                return 1;
            }

            WorkerConfig? config;
            try
            {
                config = JsonSerializer.Deserialize<WorkerConfig>(header, SignatureValidationJson.Options);
            }
            catch (JsonException)
            {
                return 1;
            }

            if (config is null)
            {
                return 1;
            }

            if (!TestModeAllowed(config.TestMode))
            {
                await WriteFrameAsync(stdout, new WorkerEnvelope(SignatureWorkerProtocol.Statuses.Error, null,
                    "TestModeNotAllowed"));
                continue;
            }

            switch (config.TestMode)
            {
                case SignatureWorkerProtocol.TestModes.Hang:
                    await Task.Delay(Timeout.InfiniteTimeSpan);
                    return 0;
                case SignatureWorkerProtocol.TestModes.Allocate:
                    AllocateUntilKilled();
                    return 0;
                case SignatureWorkerProtocol.TestModes.BadOutput:
                    await stdout.WriteAsync("this is not a validation envelope {{{"u8.ToArray());
                    await stdout.FlushAsync();
                    continue;
                case SignatureWorkerProtocol.TestModes.BypassPrecheck:
                    PdfPageTreePrecheck.DisabledForTesting = true;
                    break;
                case not null:
                    await WriteFrameAsync(stdout, new WorkerEnvelope(SignatureWorkerProtocol.Statuses.Error, null,
                        "UnknownTestMode"));
                    continue;
            }

            var envelope = body.Length == 0
                ? new WorkerEnvelope(SignatureWorkerProtocol.Statuses.FormatError, null,
                    "The worker received an empty document.")
                : await ProcessOneAsync(config, body);
            await WriteFrameAsync(stdout, envelope);
        }
    }

    /// <summary>
    /// Validates one document with one configuration and maps every outcome to an envelope. Never
    /// throws: the persistent loop must survive one bad request to serve the next.
    /// </summary>
    private static async Task<WorkerEnvelope> ProcessOneAsync(WorkerConfig config, byte[] document)
    {
        var options = config.ToOptions();
        var anchors = new TrustAnchorStore(options, NullLogger.Instance, config.ContentRoot);
        var revocation = new OfflineRevocationStore(options.CrlDirectory, NullLogger.Instance, config.ContentRoot);
        var service = new PdfSignatureValidationService(options, anchors, revocation, NullLogger.Instance);
        try
        {
            var response = await service.ValidateAsync(document, CancellationToken.None, config.GatewayHost);
            return new WorkerEnvelope(SignatureWorkerProtocol.Statuses.Ok, response, null);
        }
        catch (PdfEncryptedException exception)
        {
            return new WorkerEnvelope(SignatureWorkerProtocol.Statuses.Encrypted, null, exception.Message);
        }
        catch (PdfSignatureFormatException exception)
        {
            return new WorkerEnvelope(SignatureWorkerProtocol.Statuses.FormatError, null, exception.Message);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            // Only the type name crosses the process boundary: exception messages may quote
            // library internals, and the parent reports a fixed text anyway.
            return new WorkerEnvelope(SignatureWorkerProtocol.Statuses.Error, null, exception.GetType().Name);
        }
    }

    private static bool TestModeAllowed(string? testMode) =>
        testMode is null || string.Equals(
            Environment.GetEnvironmentVariable(SignatureWorkerProtocol.TestEnvironmentVariable), "1",
            StringComparison.Ordinal);

    /// <summary>
    /// Reads exactly <paramref name="count"/> bytes, or returns null on EOF. The caller treats EOF
    /// on the first header-length read as a clean retire and EOF anywhere else as a framing error.
    /// </summary>
    private static async Task<byte[]?> ReadExactAsync(Stream stream, int count)
    {
        var buffer = new byte[count];
        var filled = 0;
        while (filled < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(filled, count - filled));
            if (read == 0)
            {
                return null;
            }

            filled += read;
        }

        return buffer;
    }

    private static async Task WriteFrameAsync(Stream stdout, WorkerEnvelope envelope)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(envelope, SignatureValidationJson.Options);
        await stdout.WriteAsync(BitConverter.GetBytes((long)json.Length));
        await stdout.WriteAsync(json);
        await stdout.FlushAsync();
    }

    private static void AllocateUntilKilled()
    {
        // Small blocks with pauses: the parent's working-set monitor must observe the growth and
        // kill the process (the named memory failure) long before the runtime itself runs dry.
        var blocks = new List<byte[]>();
        while (true)
        {
            var block = new byte[8 * 1024 * 1024];
            block.AsSpan().Fill(1);
            blocks.Add(block);
            GC.KeepAlive(blocks);
            Thread.Sleep(20);
        }
    }

    private static async Task DrainStdinAsync()
    {
        var stdin = Console.OpenStandardInput();
        var buffer = new byte[65536];
        int read;
        do
        {
            read = await stdin.ReadAsync(buffer);
        }
        while (read > 0);
    }

    private static async Task<byte[]> ReadStdinAsync()
    {
        var stdin = Console.OpenStandardInput();
        using var collected = new MemoryStream();
        var buffer = new byte[65536];
        int read;
        while ((read = await stdin.ReadAsync(buffer)) > 0)
        {
            collected.Write(buffer, 0, read);
            if (collected.Length > SignatureWorkerProtocol.MaxStdinBytes)
            {
                throw new IOException("Document exceeds the worker stdin limit.");
            }
        }

        return collected.ToArray();
    }

    private static async Task WriteEnvelopeAsync(WorkerEnvelope envelope)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(envelope, SignatureValidationJson.Options);
        await WriteStdoutAsync(json);
    }

    private static async Task WriteStdoutAsync(byte[] bytes)
    {
        // Raw stream writes: Console.WriteLine would re-encode non-ASCII signer names through the
        // console code page and corrupt the envelope.
        var stdout = Console.OpenStandardOutput();
        await stdout.WriteAsync(bytes);
        await stdout.FlushAsync();
    }

    /// <summary>Worker configuration as parsed from the parent's argv.</summary>
    internal sealed record WorkerConfig(
        SignatureRevocationMode RevocationMode,
        int RevocationTimeoutSeconds,
        string[] RevocationHostAllowList,
        bool UseWindowsTrustedRoots,
        string? ExtraAnchorsDirectory,
        string? CrlDirectory,
        string ContentRoot,
        string? GatewayHost,
        string? TestMode)
    {
        public static WorkerConfig Parse(string[] args)
        {
            string? Value(string name)
            {
                for (var i = 0; i + 1 < args.Length; i++)
                {
                    if (string.Equals(args[i], name, StringComparison.Ordinal))
                    {
                        return args[i + 1];
                    }
                }

                return null;
            }

            var mode = Value(SignatureWorkerProtocol.Arguments.RevocationMode);
            if (!Enum.TryParse<SignatureRevocationMode>(mode, ignoreCase: true, out var revocationMode))
            {
                throw new ArgumentException("Missing or invalid --revocation-mode.");
            }

            var contentRoot = Value(SignatureWorkerProtocol.Arguments.ContentRoot);
            if (string.IsNullOrWhiteSpace(contentRoot))
            {
                throw new ArgumentException("Missing --content-root.");
            }

            var timeoutText = Value(SignatureWorkerProtocol.Arguments.RevocationTimeoutSeconds);
            if (!int.TryParse(timeoutText, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var timeoutSeconds))
            {
                throw new ArgumentException("Missing or invalid --revocation-timeout-seconds.");
            }

            var allowListText = Value(SignatureWorkerProtocol.Arguments.RevocationHostAllowList);
            var allowList = string.IsNullOrEmpty(allowListText)
                ? []
                : allowListText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var rootsText = Value(SignatureWorkerProtocol.Arguments.UseWindowsRoots);
            if (!bool.TryParse(rootsText, out var useWindowsRoots))
            {
                throw new ArgumentException("Missing or invalid --use-windows-roots.");
            }

            static string? EmptyToNull(string? value) => string.IsNullOrEmpty(value) ? null : value;

            return new WorkerConfig(
                revocationMode,
                timeoutSeconds,
                allowList,
                useWindowsRoots,
                EmptyToNull(Value(SignatureWorkerProtocol.Arguments.AnchorsDirectory)),
                EmptyToNull(Value(SignatureWorkerProtocol.Arguments.CrlDirectory)),
                contentRoot,
                EmptyToNull(Value(SignatureWorkerProtocol.Arguments.GatewayHost)),
                EmptyToNull(Value(SignatureWorkerProtocol.Arguments.TestMode)));
        }

        public SignatureValidationOptions ToOptions() => new()
        {
            Enabled = true,
            RevocationMode = RevocationMode,
            RevocationTimeoutSeconds = RevocationTimeoutSeconds,
            RevocationHostAllowList = RevocationHostAllowList,
            UseWindowsTrustedRoots = UseWindowsTrustedRoots,
            ExtraAnchorsDirectory = ExtraAnchorsDirectory,
            CrlDirectory = CrlDirectory
        };

        /// <summary>Builds the per-request header the parent sends a persistent worker.</summary>
        public static WorkerConfig FromOptions(
            SignatureValidationOptions options, string contentRoot, string? gatewayHost) =>
            new(
                options.RevocationMode,
                Math.Clamp(options.RevocationTimeoutSeconds, 1, 30),
                options.RevocationHostAllowList ?? [],
                options.UseWindowsTrustedRoots,
                options.ExtraAnchorsDirectory,
                options.CrlDirectory,
                contentRoot,
                gatewayHost,
                options.WorkerTestMode);
    }
}
