using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace ODVGateway.Services.Signatures;

/// <summary>
/// Parent side of isolated signature validation: runs the same assembly with
/// <c>--signature-worker</c> and reads one JSON <see cref="WorkerEnvelope"/> per document.
/// Production requests use a small warm pool of persistent workers (length-prefixed frames over
/// the same stdin/stdout pipes): at most two idle workers are retained, each is retired after 25
/// requests, after five idle minutes, or on any failure. Fault-injection requests (any test mode)
/// always use a fresh one-shot worker that exits after one document, so a test worker is never
/// reused. Concurrency is bounded by the endpoint's <c>SignatureValidationLimiter</c>, which is
/// held across the whole worker run, so this client needs no limiter of its own. No document bytes
/// ever touch disk: stdin/stdout pipes only.
/// </summary>
public sealed class SignatureWorkerClient : IDisposable
{
    private readonly ILogger logger;
    private readonly string contentRootPath;
    private readonly object idleLock = new();
    private readonly List<PooledWorker> idle = new();
    private int spawnedWorkerCount;
    private bool disposed;

    public SignatureWorkerClient(ILogger logger, string contentRootPath)
    {
        this.logger = logger;
        this.contentRootPath = contentRootPath;
        IsolatedTempRoot = TryCreateIsolatedTempRoot(logger);
    }

    /// <summary>
    /// Private temp directory every worker of this client is pointed at (<c>TMP</c>/<c>TEMP</c>/
    /// <c>TMPDIR</c>). The worker never needs temp files — document bytes travel over pipes — so
    /// the directory doubles as the regression tripwire: any entry in it after a run means bytes
    /// touched the disk. Null when the directory could not be created, in which case workers
    /// inherit the process temp and validation proceeds anyway.
    /// </summary>
    internal string? IsolatedTempRoot { get; }

    private static string? TryCreateIsolatedTempRoot(ILogger logger)
    {
        try
        {
            var root = Path.Combine(Path.GetTempPath(), "odv-sigworker-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return root;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or NotSupportedException)
        {
            logger.LogWarning(exception,
                "Signature validation workers share the process temp directory. Exception={ExceptionType}",
                exception.GetType().Name);
            return null;
        }
    }

    /// <summary>Workers spawned by this client. Test observability for pool reuse.</summary>
    internal int SpawnedWorkerCount => spawnedWorkerCount;

    /// <summary>Workers currently retained idle. Test observability for pool reuse.</summary>
    internal int IdleWorkerCount
    {
        get
        {
            lock (idleLock)
            {
                return idle.Count;
            }
        }
    }

    /// <summary>
    /// Validates one document in a worker process. Returns the same report as the in-process path;
    /// throws <see cref="PdfEncryptedException"/> and <see cref="PdfSignatureFormatException"/>
    /// for unreadable documents (reconstructed from the worker's envelope) and
    /// <see cref="SignatureWorkerException"/> when the worker itself fails.
    /// </summary>
    public Task<Models.PdfSignatureValidationResponse> ValidateAsync(
        Options.SignatureValidationOptions options,
        byte[] fileBytes,
        string? gatewayHost,
        CancellationToken cancellationToken = default) =>
        options.WorkerTestMode is null
            ? ValidatePooledAsync(options, fileBytes, gatewayHost, cancellationToken)
            : ValidateOneShotAsync(options, fileBytes, gatewayHost, cancellationToken);

    /// <summary>
    /// Test-only: runs a fault-injection request through the pooled path, so the pool's own
    /// timeout, memory and crash supervision is covered. Production fault-injection requests
    /// always use <see cref="ValidateOneShotAsync"/> and are never pooled.
    /// </summary>
    internal Task<Models.PdfSignatureValidationResponse> ValidatePooledForTestingAsync(
        Options.SignatureValidationOptions options,
        byte[] fileBytes,
        string? gatewayHost,
        CancellationToken cancellationToken = default) =>
        ValidatePooledAsync(options, fileBytes, gatewayHost, cancellationToken);

    public void Dispose()
    {
        List<PooledWorker> retained;
        lock (idleLock)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            retained = new List<PooledWorker>(idle);
            idle.Clear();
        }

        foreach (var worker in retained)
        {
            worker.Destroy();
        }

        if (IsolatedTempRoot is not null)
        {
            try
            {
                Directory.Delete(IsolatedTempRoot, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception,
                    "Signature validation worker temp directory could not be removed. Exception={ExceptionType}",
                    exception.GetType().Name);
            }
        }
    }

    private async Task<Models.PdfSignatureValidationResponse> ValidatePooledAsync(
        Options.SignatureValidationOptions options,
        byte[] fileBytes,
        string? gatewayHost,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var start = Stopwatch.GetTimestamp();
        var timeout = TimeSpan.FromSeconds(Math.Clamp(options.WorkerTimeoutSeconds, 1, 120));
        var memoryCap = Math.Clamp(options.WorkerMaxMemoryBytes, 32L * 1024L * 1024L, 8L * 1024L * 1024L * 1024L);

        var worker = Rent() ?? Spawn(persistent: true, options, gatewayHost);
        var settled = false;
        try
        {
            var header = JsonSerializer.SerializeToUtf8Bytes(
                SignatureWorkerMain.WorkerConfig.FromOptions(options, contentRootPath, gatewayHost),
                Models.SignatureValidationJson.Options);
            try
            {
                await WriteFrameAsync(worker.Process.StandardInput.BaseStream, header, fileBytes, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            {
                throw SignatureWorkerException.Crashed();
            }

            var (frame, peakMemoryBytes) = await ReadFrameSupervisedAsync(worker, start, timeout, memoryCap, cancellationToken);
            WorkerEnvelope? envelope;
            try
            {
                envelope = frame.Length == 0
                    ? null
                    : JsonSerializer.Deserialize<WorkerEnvelope>(frame, Models.SignatureValidationJson.Options);
            }
            catch (JsonException)
            {
                envelope = null;
            }

            var mapped = MapEnvelope(envelope, pooled: true, worker.Uses + 1, ExitCode(worker.Process),
                start, peakMemoryBytes, frame.Length, worker.StderrBytes);
            if (mapped.HealthyWorker && !HasExited(worker.Process))
            {
                Return(worker);
            }
            else
            {
                worker.Destroy();
            }

            settled = true;
            if (mapped.Failure is not null)
            {
                throw mapped.Failure;
            }

            return mapped.Response!;
        }
        catch (SignatureWorkerException exception)
        {
            if (!settled)
            {
                // Read before destroying: afterwards the exit code is -1 and the peak is 0.
                var exitCode = ExitCode(worker.Process);
                var peakMemoryBytes = PeakWorkingSetBytes(worker.Process);
                worker.Destroy();
                LogOutcome(WorkerOutcome(exception.Code), exitCode, pooled: true,
                    worker.Uses + 1, start, peakMemoryBytes, stdoutBytes: 0,
                    worker.StderrBytes);
            }

            throw;
        }
        catch
        {
            if (!settled)
            {
                worker.Destroy();
            }

            throw;
        }
    }

    private static string WorkerOutcome(string code) =>
        code switch
        {
            SignatureValidationReasons.ValidationWorkerTimeout => "timeout",
            SignatureValidationReasons.ValidationWorkerMemory => "memory",
            _ => "crashed"
        };

    private async Task<Models.PdfSignatureValidationResponse> ValidateOneShotAsync(
        Options.SignatureValidationOptions options,
        byte[] fileBytes,
        string? gatewayHost,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var start = Stopwatch.GetTimestamp();
        var timeout = TimeSpan.FromSeconds(Math.Clamp(options.WorkerTimeoutSeconds, 1, 120));
        var memoryCap = Math.Clamp(options.WorkerMaxMemoryBytes, 32L * 1024L * 1024L, 8L * 1024L * 1024L * 1024L);

        using var worker = Spawn(persistent: false, options, gatewayHost);
        var process = worker.Process;
        var stdoutTask = ReadCappedAsync(process.StandardOutput.BaseStream, SignatureWorkerProtocol.MaxStdoutBytes);

        try
        {
            await process.StandardInput.BaseStream.WriteAsync(fileBytes, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException
            or OperationCanceledException)
        {
            // The child died before reading stdin, or the request was cancelled: fall through to
            // exit handling (crash mapping) or cancellation below.
            if (cancellationToken.IsCancellationRequested)
            {
                Kill(process);
                throw new OperationCanceledException(cancellationToken);
            }
        }
        // A worker that died before reading stdin leaves a broken pipe: closing it must not
        // escape the crash mapping, so close failures are swallowed here and named below.
        finally
        {
            try
            {
                process.StandardInput.Close();
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            {
            }
        }

        long peakMemoryBytes = 0;
        var killedForTimeout = false;
        var killedForMemory = false;
        try
        {
            while (!HasExited(process))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Stopwatch.GetElapsedTime(start) >= timeout)
                {
                    killedForTimeout = true;
                    Kill(process);
                    break;
                }

                peakMemoryBytes = Math.Max(peakMemoryBytes, WorkingSetBytes(process));
                if (peakMemoryBytes > memoryCap)
                {
                    killedForMemory = true;
                    Kill(process);
                    break;
                }

                await Task.Delay(SignatureWorkerProtocol.MemoryPollInterval, cancellationToken);
            }

            peakMemoryBytes = Math.Max(peakMemoryBytes, PeakWorkingSetBytes(process));
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }

        var stdout = await stdoutTask;
        var exitCode = ExitCode(process);

        if (killedForTimeout)
        {
            LogOutcome("timeout", exitCode, pooled: false, workerUses: 1, start,
                peakMemoryBytes, stdout.Length, worker.StderrBytes);
            throw SignatureWorkerException.TimedOut();
        }

        if (killedForMemory)
        {
            LogOutcome("memory", exitCode, pooled: false, workerUses: 1, start,
                peakMemoryBytes, stdout.Length, worker.StderrBytes);
            throw SignatureWorkerException.OutOfMemory();
        }

        WorkerEnvelope? envelope = null;
        if (!stdout.Truncated && exitCode == 0 && stdout.Length > 0)
        {
            try
            {
                envelope = JsonSerializer.Deserialize<WorkerEnvelope>(stdout.Bytes, Models.SignatureValidationJson.Options);
            }
            catch (JsonException)
            {
            }
        }

        var mapped = MapEnvelope(envelope, pooled: false, workerUses: 1, exitCode, start,
            peakMemoryBytes, stdout.Length, worker.StderrBytes);
        if (mapped.Failure is not null)
        {
            throw mapped.Failure;
        }

        return mapped.Response!;
    }

    /// <summary>
    /// Maps a worker answer to a response or a typed failure, and logs the one structured line
    /// for this request. A null envelope (empty, truncated, or unparseable output) is a crash.
    /// </summary>
    private MappedEnvelope MapEnvelope(
        WorkerEnvelope? envelope, bool pooled, int workerUses, int exitCode, long startTimestamp,
        long peakMemoryBytes, int stdoutBytes, long stderrBytes)
    {
        var outcome = envelope switch
        {
            { Status: SignatureWorkerProtocol.Statuses.Ok } when envelope.Response is not null => "ok",
            { Status: SignatureWorkerProtocol.Statuses.Encrypted } => "encrypted",
            { Status: SignatureWorkerProtocol.Statuses.FormatError } => "format-error",
            { Status: SignatureWorkerProtocol.Statuses.Error } => "error",
            _ => "crashed"
        };
        LogOutcome(outcome, exitCode, pooled, workerUses, startTimestamp, peakMemoryBytes, stdoutBytes, stderrBytes);

        return outcome switch
        {
            "ok" => new MappedEnvelope(HealthyWorker: true, envelope!.Response, Failure: null),
            "encrypted" => new MappedEnvelope(true, null,
                new PdfEncryptedException(envelope!.Error ?? "The PDF is encrypted.")),
            "format-error" => new MappedEnvelope(true, null,
                new PdfSignatureFormatException(envelope!.Error ?? "The PDF could not be parsed.")),
            "error" => new MappedEnvelope(false, null, new InvalidOperationException(
                "Signature validation worker failed: " + (envelope!.Error ?? "unknown") + ".")),
            _ => new MappedEnvelope(false, null, SignatureWorkerException.Crashed())
        };
    }

    private sealed record MappedEnvelope(
        bool HealthyWorker, Models.PdfSignatureValidationResponse? Response, Exception? Failure);

    private PooledWorker? Rent()
    {
        List<PooledWorker>? garbage = null;
        PooledWorker? rented = null;
        lock (idleLock)
        {
            while (idle.Count > 0 && rented is null)
            {
                var candidate = idle[^1];
                idle.RemoveAt(idle.Count - 1);
                if (!HasExited(candidate.Process) &&
                    DateTimeOffset.UtcNow - candidate.IdleSinceUtc <= SignatureWorkerProtocol.IdleWorkerExpiry)
                {
                    rented = candidate;
                }
                else
                {
                    (garbage ??= new()).Add(candidate);
                }
            }
        }

        // Destroy outside the lock: reaping a process can block.
        if (garbage is not null)
        {
            foreach (var worker in garbage)
            {
                worker.Destroy();
            }
        }

        return rented;
    }

    private void Return(PooledWorker worker)
    {
        worker.Uses++;
        var retire = worker.Uses >= SignatureWorkerProtocol.MaxRequestsPerWorker || HasExited(worker.Process);
        lock (idleLock)
        {
            if (!retire && !disposed && idle.Count < SignatureWorkerProtocol.MaxIdleWorkers)
            {
                worker.IdleSinceUtc = DateTimeOffset.UtcNow;
                idle.Add(worker);
                return;
            }

            retire = true;
        }

        if (retire)
        {
            worker.RetireGracefully();
        }
    }

    private PooledWorker Spawn(bool persistent, Options.SignatureValidationOptions options, string? gatewayHost)
    {
        var (fileName, prefixArgs) = ResolveWorkerCommand();
        var process = new Process();
        process.StartInfo.FileName = fileName;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.RedirectStandardInput = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        foreach (var prefix in prefixArgs)
        {
            process.StartInfo.ArgumentList.Add(prefix);
        }

        if (persistent)
        {
            process.StartInfo.ArgumentList.Add(SignatureWorkerProtocol.WorkerArgument);
            process.StartInfo.ArgumentList.Add(SignatureWorkerProtocol.PersistentArgument);
        }
        else
        {
            AddWorkerArguments(process, options, gatewayHost);
        }

        if (IsolatedTempRoot is not null)
        {
            process.StartInfo.EnvironmentVariables["TMP"] = IsolatedTempRoot;
            process.StartInfo.EnvironmentVariables["TEMP"] = IsolatedTempRoot;
            process.StartInfo.EnvironmentVariables["TMPDIR"] = IsolatedTempRoot;
        }

        try
        {
            process.Start();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception
            or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            process.Dispose();
            throw new InvalidOperationException("The signature validation worker could not be started.", exception);
        }

        var worker = new PooledWorker(process, WorkerJobObject.TryAssign(process));
        Interlocked.Increment(ref spawnedWorkerCount);
        return worker;
    }

    private void AddWorkerArguments(Process process, Options.SignatureValidationOptions options, string? gatewayHost)
    {
        var list = process.StartInfo.ArgumentList;
        list.Add(SignatureWorkerProtocol.WorkerArgument);
        list.Add(SignatureWorkerProtocol.Arguments.RevocationMode);
        list.Add(options.RevocationMode.ToString());
        list.Add(SignatureWorkerProtocol.Arguments.RevocationTimeoutSeconds);
        list.Add(Math.Clamp(options.RevocationTimeoutSeconds, 1, 30).ToString(CultureInfo.InvariantCulture));
        list.Add(SignatureWorkerProtocol.Arguments.RevocationHostAllowList);
        list.Add(string.Join(",", options.RevocationHostAllowList ?? []));
        list.Add(SignatureWorkerProtocol.Arguments.UseWindowsRoots);
        list.Add(options.UseWindowsTrustedRoots ? "true" : "false");
        list.Add(SignatureWorkerProtocol.Arguments.AnchorsDirectory);
        list.Add(options.ExtraAnchorsDirectory ?? string.Empty);
        list.Add(SignatureWorkerProtocol.Arguments.CrlDirectory);
        list.Add(options.CrlDirectory ?? string.Empty);
        list.Add(SignatureWorkerProtocol.Arguments.ContentRoot);
        list.Add(contentRootPath);
        list.Add(SignatureWorkerProtocol.Arguments.GatewayHost);
        list.Add(gatewayHost ?? string.Empty);
        list.Add(SignatureWorkerProtocol.Arguments.TestMode);
        list.Add(options.WorkerTestMode ?? string.Empty);
    }

    /// <summary>
    /// The worker command: this same process image when it already is ODVGateway (production, IIS
    /// out-of-process, Kestrel), else the ODVGateway apphost next to the entry assembly, else the
    /// framework-dependent DLL through the <c>dotnet</c> host (tests, <c>dotnet run</c>).
    /// </summary>
    internal static (string FileName, string[] PrefixArgs) ResolveWorkerCommand()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(processPath) &&
            string.Equals(Path.GetFileNameWithoutExtension(processPath), "ODVGateway",
                StringComparison.OrdinalIgnoreCase))
        {
            return (processPath, []);
        }

        var baseDirectory = AppContext.BaseDirectory;
        var host = Path.Combine(baseDirectory, OperatingSystem.IsWindows() ? "ODVGateway.exe" : "ODVGateway");
        if (File.Exists(host))
        {
            return (host, []);
        }

        var managed = Path.Combine(baseDirectory, "ODVGateway.dll");
        if (File.Exists(managed))
        {
            return ("dotnet", [managed]);
        }

        throw new InvalidOperationException("Cannot locate the ODVGateway worker executable.");
    }

    private static async Task WriteFrameAsync(Stream stdin, byte[] header, byte[] body, CancellationToken cancellationToken)
    {
        await stdin.WriteAsync(BitConverter.GetBytes(header.Length), cancellationToken);
        await stdin.WriteAsync(header, cancellationToken);
        await stdin.WriteAsync(BitConverter.GetBytes((long)body.Length), cancellationToken);
        await stdin.WriteAsync(body, cancellationToken);
        await stdin.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// Reads one response frame while supervising the worker: the wall-clock timeout, the
    /// working-set cap, and parent cancellation all kill the worker. A worker that exits before
    /// completing the frame, or a frame that cannot be parsed, fails the request as crashed.
    /// </summary>
    private async Task<(byte[] Frame, long PeakMemoryBytes)> ReadFrameSupervisedAsync(
        PooledWorker worker, long startTimestamp, TimeSpan timeout, long memoryCap,
        CancellationToken cancellationToken)
    {
        var readTask = ReadFrameAsync(worker.Process.StandardOutput.BaseStream);
        long peakMemoryBytes = 0;
        while (!readTask.IsCompleted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Stopwatch.GetElapsedTime(startTimestamp) >= timeout)
            {
                throw SignatureWorkerException.TimedOut();
            }

            peakMemoryBytes = Math.Max(peakMemoryBytes, WorkingSetBytes(worker.Process));
            if (peakMemoryBytes > memoryCap)
            {
                throw SignatureWorkerException.OutOfMemory();
            }

            if (HasExited(worker.Process))
            {
                break;
            }

            await Task.Delay(SignatureWorkerProtocol.MemoryPollInterval, cancellationToken);
        }

        peakMemoryBytes = Math.Max(peakMemoryBytes, PeakWorkingSetBytes(worker.Process));
        try
        {
            return (await readTask, peakMemoryBytes);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            throw SignatureWorkerException.Crashed();
        }
    }

    private static async Task<byte[]> ReadFrameAsync(Stream stream)
    {
        var lengthBytes = await ReadExactAsync(stream, sizeof(long));
        var length = BitConverter.ToInt64(lengthBytes);
        if (length < 0 || length > SignatureWorkerProtocol.MaxStdoutBytes)
        {
            throw new IOException("Invalid worker response frame length.");
        }

        return await ReadExactAsync(stream, (int)length);
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int count)
    {
        var buffer = new byte[count];
        var filled = 0;
        while (filled < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(filled, count - filled));
            if (read == 0)
            {
                throw new IOException("Unexpected end of worker response frame.");
            }

            filled += read;
        }

        return buffer;
    }

    private static async Task<(byte[] Bytes, int Length, bool Truncated)> ReadCappedAsync(Stream stream, int cap)
    {
        using var collected = new MemoryStream();
        var buffer = new byte[65536];
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0)
        {
            var room = cap - (int)collected.Length;
            if (read > room)
            {
                if (room > 0)
                {
                    collected.Write(buffer, 0, room);
                }

                // Drain the rest so the child never blocks on a full pipe, then report truncation.
                while (await stream.ReadAsync(buffer) > 0)
                {
                }

                return (collected.ToArray(), (int)collected.Length, true);
            }

            collected.Write(buffer, 0, read);
        }

        return (collected.ToArray(), (int)collected.Length, false);
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static int ExitCode(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode : -1;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
    }

    private static long WorkingSetBytes(Process process)
    {
        try
        {
            process.Refresh();
            return process.HasExited ? 0 : process.WorkingSet64;
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return 0;
        }
    }

    private static long PeakWorkingSetBytes(Process process)
    {
        try
        {
            process.Refresh();
            return process.PeakWorkingSet64;
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return 0;
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }

        try
        {
            process.WaitForExit(TimeSpan.FromSeconds(5));
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
        }
    }

    private void LogOutcome(string outcome, int exitCode, bool pooled, int workerUses, long startTimestamp,
        long peakMemoryBytes, int stdoutBytes, long stderrBytes)
    {
        var durationMs = (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        if (string.Equals(outcome, "ok", StringComparison.Ordinal) ||
            string.Equals(outcome, "encrypted", StringComparison.Ordinal) ||
            string.Equals(outcome, "format-error", StringComparison.Ordinal))
        {
            logger.LogInformation(
                "Signature validation worker finished. Outcome={Outcome} ExitCode={ExitCode} Pooled={Pooled} WorkerUses={WorkerUses} DurationMs={DurationMs} PeakMemoryBytes={PeakMemoryBytes} StdoutBytes={StdoutBytes} StderrBytes={StderrBytes}",
                outcome, exitCode, pooled, workerUses, durationMs, peakMemoryBytes, stdoutBytes, stderrBytes);
        }
        else
        {
            logger.LogWarning(
                "Signature validation worker failed. Outcome={Outcome} ExitCode={ExitCode} Pooled={Pooled} WorkerUses={WorkerUses} DurationMs={DurationMs} PeakMemoryBytes={PeakMemoryBytes} StdoutBytes={StdoutBytes} StderrBytes={StderrBytes}",
                outcome, exitCode, pooled, workerUses, durationMs, peakMemoryBytes, stdoutBytes, stderrBytes);
        }
    }

    /// <summary>
    /// One worker process owned by the client: the process, its kill-on-close job, its stderr
    /// drain, and its pool accounting. Stderr is drained for the whole worker lifetime (counted,
    /// never stored) so a chatty worker can never block on a full pipe.
    /// </summary>
    private sealed class PooledWorker : IDisposable
    {
        private int disposed;

        public PooledWorker(Process process, WorkerJobObject? job)
        {
            Process = process;
            Job = job;
            StderrDrain = DrainStderrAsync(process);
        }

        public Process Process { get; }

        public WorkerJobObject? Job { get; }

        public Task<long> StderrDrain { get; }

        public int Uses;

        public DateTimeOffset IdleSinceUtc = DateTimeOffset.UtcNow;

        public long StderrBytes => StderrDrain.IsCompletedSuccessfully ? StderrDrain.Result : 0;

        /// <summary>Kills the worker immediately. Used for failures and shutdown.</summary>
        public void Destroy()
        {
            Kill(Process);
            Dispose();
        }

        /// <summary>
        /// Lets a healthy worker exit cleanly: closing stdin reads as EOF in its request loop.
        /// Falls back to killing a worker that does not exit in time.
        /// </summary>
        public void RetireGracefully()
        {
            try
            {
                Process.StandardInput.Close();
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException)
            {
            }

            try
            {
                if (!Process.WaitForExit(TimeSpan.FromSeconds(5)))
                {
                    Kill(Process);
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException
                or System.ComponentModel.Win32Exception)
            {
            }

            Dispose();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            Job?.Dispose();
            Process.Dispose();
        }

        private static async Task<long> DrainStderrAsync(Process process)
        {
            long counted = 0;
            var buffer = new byte[65536];
            try
            {
                int read;
                while ((read = await process.StandardError.BaseStream.ReadAsync(buffer)) > 0)
                {
                    counted += read;
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException
                or OperationCanceledException)
            {
            }

            return counted;
        }
    }
}
