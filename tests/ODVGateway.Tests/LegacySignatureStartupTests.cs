using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ODVGateway.Tests;

/// <summary>
/// True process-boot proof that a site upgrading with a legacy <c>ODVGateway:signatures</c> block
/// still in its own <c>appsettings.json</c> starts and serves documents. WebApplicationFactory test
/// configuration never reaches the eager startup options bind in Program.cs (it only feeds
/// request-time options, which <see cref="LegacySignatureConfigurationTests"/> covers), so this suite
/// boots the real ODVGateway.dll in a child process whose content root holds the shipped
/// appsettings.json plus the legacy block. A strict startup bind (<c>ErrorOnUnknownConfiguration</c>)
/// makes the child exit before Kestrel binds, failing here.
/// </summary>
/// <remarks>
/// Every answer is tied to the child this test started: the child's <c>maxSourceProxyBytes</c> is a
/// fresh random per-test value that its /health echoes, so another gateway on the same port (a
/// parallel run of this suite in the window between port pick and bind) cannot produce it, and
/// /health must still carry it, with the child still running, after the scenario's requests.
/// </remarks>
public sealed class LegacySignatureStartupTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Startup_WithLegacySignaturesBlockInAppSettings_StaysUpAndAnswersHttp(bool enabled)
    {
        using var site = TestSite.Create();
        await site.WriteUpgradedSiteAppSettingsAsync(enabled, trustDirectSources: false);

        var boot = await BootGatewayOnFreeLoopbackPortAsync(site, scenario: null);

        AssertServedByOwnChild(site, boot);
    }

    [Fact]
    public async Task Startup_WithEnabledLegacySignaturesBlock_ServesADocumentThroughTheSessionPath()
    {
        using var site = TestSite.Create();
        await site.WriteUpgradedSiteAppSettingsAsync(enabled: true, trustDirectSources: true);
        var document = CreateSyntheticPdf();
        var documentPath = Path.Join(site.SourcesDirectory, "legacy-signatures-check.pdf");
        await File.WriteAllBytesAsync(documentPath, document, TestContext.Current.CancellationToken);

        HttpStatusCode? prepStatus = null;
        string? sessionKey = null;
        HttpStatusCode? sourceStatus = null;
        string? sourceContentType = null;
        byte[]? served = null;
        var boot = await BootGatewayOnFreeLoopbackPortAsync(site, async (client, cancellationToken) =>
        {
            // The same /prep payload shape WebClient posts: one document whose file ticket is
            // "fileId|extension|path", read back through the direct trusted-root source path.
            var prep = new JsonObject
            {
                ["userId"] = "legacy-user",
                ["sessionId"] = "legacy-session",
                ["portableDocuments"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["documentId"] = "legacy-document",
                        ["fileData"] = new JsonArray { "legacy-file|pdf|" + documentPath }
                    }
                }
            };
            using var prepResponse = await client.PostAsync(
                "/prep", new StringContent(prep.ToJsonString(), Encoding.UTF8, "application/json"), cancellationToken);
            prepStatus = prepResponse.StatusCode;
            if (prepResponse.StatusCode != HttpStatusCode.OK)
            {
                return;
            }

            var prepared = await prepResponse.Content.ReadFromJsonAsync<JsonObject>(cancellationToken);
            sessionKey = prepared?["sessionKey"]?.GetValue<string>();
            if (string.IsNullOrEmpty(sessionKey))
            {
                return;
            }

            using var sourceResponse = await client.GetAsync(
                $"/source/{Uri.EscapeDataString(sessionKey)}/0", cancellationToken);
            sourceStatus = sourceResponse.StatusCode;
            sourceContentType = sourceResponse.Content.Headers.ContentType?.MediaType;
            served = await sourceResponse.Content.ReadAsByteArrayAsync(cancellationToken);
        });

        AssertServedByOwnChild(site, boot);
        Assert.Equal(HttpStatusCode.OK, prepStatus);
        Assert.False(string.IsNullOrEmpty(sessionKey), "/prep answered without a session key. Output:\n" + boot.Output);
        Assert.Equal(HttpStatusCode.OK, sourceStatus);
        Assert.Equal("application/pdf", sourceContentType);
        Assert.Equal(document, served);
    }

    private static void AssertServedByOwnChild(TestSite site, BootResult boot)
    {
        Assert.False(boot.TimedOut, "The gateway child neither started nor exited. Output:\n" + boot.Output);
        Assert.True(boot.Booted, $"The gateway exited during startup (exit code {boot.ExitCode}). Output:\n" + boot.Output);
        Assert.True(
            boot.AnsweredByChild,
            "Something answered /health, but not with this child's identity marker and the probe's "
            + $"{CorrelationIdHeader}, so the answer came from another listener. Output:\n" + boot.Output);
        Assert.Equal(HttpStatusCode.OK, boot.HealthStatus);
        Assert.Equal("ok", boot.HealthStatusValue);
        Assert.True(
            boot.AnsweredByChildAfterScenario,
            "/health no longer carried this child's identity marker after the scenario. Output:\n" + boot.Output);
        Assert.True(boot.StillRunningAfterScenario, "The gateway exited after answering HTTP. Output:\n" + boot.Output);
        Assert.True(
            Directory.Exists(site.LogDirectory) && Directory.EnumerateFiles(site.LogDirectory, "*.log").Any(),
            $"The child wrote no log file into the test's own log directory {site.LogDirectory}. Output:\n" + boot.Output);
    }

    /// <summary>
    /// A small PDF-shaped payload with random content, so the byte comparison proves the gateway
    /// streamed this exact file rather than any document it might have at hand.
    /// </summary>
    private static byte[] CreateSyntheticPdf()
    {
        var header = Encoding.ASCII.GetBytes("%PDF-1.4\n% synthetic test document\n");
        var trailer = Encoding.ASCII.GetBytes("\n%%EOF\n");
        var body = RandomNumberGenerator.GetBytes(4096);
        return [.. header, .. body, .. trailer];
    }

    /// <summary>
    /// The child's whole world lives in one temp folder: content root and appsettings.json, the
    /// dist folder that keeps /health "ok", the trusted source root, and the NLog directory, so
    /// nothing the child writes lands outside what the test deletes.
    /// </summary>
    private sealed class TestSite : IDisposable
    {
        private const long ShippedMaxSourceProxyBytes = 64L * 1024 * 1024;

        private TestSite(string root)
        {
            Root = root;
            DistPath = Path.Join(root, "dist");
            SourcesDirectory = Path.Join(root, "sources");
            LogDirectory = Path.Join(root, "logs");
        }

        public string Root { get; }

        public string DistPath { get; }

        public string SourcesDirectory { get; }

        public string LogDirectory { get; }

        /// <summary>
        /// A random <c>maxSourceProxyBytes</c> above the shipped 64 MiB default. /health echoes the
        /// value unchanged whether or not the dist folder resolves, and no other process has it.
        /// </summary>
        public long IdentityMarker { get; } = ShippedMaxSourceProxyBytes
            + RandomNumberGenerator.GetInt32(1, int.MaxValue - (int)ShippedMaxSourceProxyBytes);

        public static TestSite Create()
        {
            var site = new TestSite(
                Path.Join(Path.GetTempPath(), "odvgateway-startup-tests-" + Guid.NewGuid().ToString("N")));
            try
            {
                Directory.CreateDirectory(site.DistPath);
                Directory.CreateDirectory(site.SourcesDirectory);
                File.WriteAllText(Path.Join(site.DistPath, "index.html"), "<!doctype html><html></html>");
            }
            catch
            {
                site.Dispose();
                throw;
            }

            return site;
        }

        /// <summary>
        /// Writes the appsettings.json an upgraded site keeps: the shipped file with the
        /// <c>signatures</c> block that pre-removal releases shipped, plus sub-keys the removed
        /// startup validation used to refuse (a worker test mode without its gate, a worker memory
        /// cap below the file budget) and one key no release ever knew. The two directory keys are
        /// written as explicit JSON nulls, as a shipped file carries them, and read back to prove
        /// they are present. Site-local settings point the dist folder, the optional trusted
        /// source root and the log directory into this site's temp folder, and set the identity marker.
        /// </summary>
        public async Task WriteUpgradedSiteAppSettingsAsync(bool enabled, bool trustDirectSources)
        {
            var shipped = await File.ReadAllTextAsync(
                Path.Join(AppContext.BaseDirectory, "appsettings.json"), TestContext.Current.CancellationToken);
            var root = JsonNode.Parse(shipped)!.AsObject();
            root["NLog"]!["variables"]!["logDirectory"] = LogDirectory;
            var gateway = root["ODVGateway"]!.AsObject();
            gateway["openDocViewerDistPath"] = DistPath;
            gateway["requireExplicitOpenDocViewerDistPath"] = true;
            gateway["maxSourceProxyBytes"] = IdentityMarker;
            if (trustDirectSources)
            {
                gateway["trustClientFilePath"] = true;
                gateway["trustedSourceRoots"] = new JsonArray { SourcesDirectory };
            }

            gateway["signatures"] = new JsonObject
            {
                ["enabled"] = enabled,
                ["useWindowsTrustedRoots"] = true,
                ["extraAnchorsDirectory"] = null,
                ["crlDirectory"] = null,
                ["revocationMode"] = "Online",
                ["revocationTimeoutSeconds"] = 15,
                ["revocationHostAllowList"] = new JsonArray(),
                ["maxFileBytes"] = 0,
                ["maxConcurrentValidations"] = 2,
                ["isolateProcess"] = true,
                ["workerTimeoutSeconds"] = 30,
                ["workerMaxMemoryBytes"] = 1,
                ["workerTestMode"] = "hang",
                ["unknownLegacyKey"] = "ignored"
            };
            var path = Path.Join(Root, "appsettings.json");
            await File.WriteAllTextAsync(path, root.ToJsonString(), TestContext.Current.CancellationToken);

            var written = JsonNode.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken))!;
            var signatures = written["ODVGateway"]!["signatures"]!.AsObject();
            Assert.Equal(enabled, signatures["enabled"]!.GetValue<bool>());
            foreach (var nullKey in new[] { "extraAnchorsDirectory", "crlDirectory" })
            {
                Assert.True(signatures.TryGetPropertyValue(nullKey, out var value), $"'{nullKey}' is missing from the written file.");
                Assert.Null(value);
            }
        }

        /// <summary>
        /// The child is killed and drained before this runs, but Windows can release a log file
        /// handle a moment after the process is gone; retry briefly, and leave a stray temp folder
        /// behind rather than replace the test's real outcome with a cleanup exception.
        /// </summary>
        public void Dispose()
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    if (Directory.Exists(Root))
                    {
                        Directory.Delete(Root, recursive: true);
                    }

                    return;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    if (attempt == 10)
                    {
                        TestContext.Current.SendDiagnosticMessage(
                            $"Could not delete the startup-test folder {Root}: {exception.Message}");
                        return;
                    }

                    Thread.Sleep(250);
                }
            }
        }
    }

    private sealed record BootResult(
        int? ExitCode,
        string Output,
        bool Booted,
        bool TimedOut,
        HttpStatusCode? HealthStatus = null,
        string? HealthStatusValue = null,
        bool AnsweredByChild = false,
        bool AnsweredByChildAfterScenario = false,
        bool StillRunningAfterScenario = false);

    private sealed record HealthProbe(HttpStatusCode Status, string? StatusValue, bool FromChild);

    private const int MaxPortAttempts = 3;

    private const string CorrelationIdHeader = "X-Correlation-ID";

    /// <summary>
    /// Boots the child on a port that was free a moment ago. Another process can take the port
    /// between <see cref="GetFreeLoopbackPort"/> and the child's bind; only that lost race, recognised
    /// by Kestrel's address-in-use failure for exactly this port, is retried on a fresh port. Any other
    /// result, including every other startup crash, is returned at once for the caller to assert on.
    /// </summary>
    private static async Task<BootResult> BootGatewayOnFreeLoopbackPortAsync(
        TestSite site, Func<HttpClient, CancellationToken, Task>? scenario)
    {
        for (var attempt = 1; ; attempt++)
        {
            var port = GetFreeLoopbackPort();
            var boot = await BootGatewayAsync(site, port, scenario);
            if (attempt == MaxPortAttempts || !IsLostPortRace(boot, port))
            {
                return boot;
            }
        }
    }

    private static bool IsLostPortRace(BootResult boot, int port)
        => !boot.Booted
            && !boot.TimedOut
            && boot.ExitCode is not null and not 0
            && boot.Output.Contains(
                $"Failed to bind to address http://127.0.0.1:{port}: address already in use.", StringComparison.Ordinal)
            && boot.Output.Contains("AddressInUseException", StringComparison.Ordinal);

    private static async Task<BootResult> BootGatewayAsync(
        TestSite site, int port, Func<HttpClient, CancellationToken, Task>? scenario)
    {
        using var process = new Process();
        process.StartInfo.FileName = "dotnet";
        process.StartInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "ODVGateway.dll"));
        process.StartInfo.ArgumentList.Add("--urls");
        process.StartInfo.ArgumentList.Add($"http://127.0.0.1:{port}");
        process.StartInfo.ArgumentList.Add("--contentRoot");
        process.StartInfo.ArgumentList.Add(site.Root);
        process.StartInfo.WorkingDirectory = site.Root;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        // A production site has no environment-specific overlay; never let one leak in from the runner.
        process.StartInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        process.StartInfo.Environment.Remove("DOTNET_ENVIRONMENT");

        var output = new StringBuilder();
        void Append(string? line)
        {
            if (line is not null)
            {
                lock (output)
                {
                    output.AppendLine(line);
                }
            }
        }

        process.OutputDataReceived += (_, e) => Append(e.Data);
        process.ErrorDataReceived += (_, e) => Append(e.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        BootResult? result = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken, timeout.Token);
        try
        {
            result = await ExerciseChildAsync(process, site, port, scenario, linked.Token);
        }
        catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
        {
            result = new BootResult(ExitCode: null, Output: string.Empty, Booted: false, TimedOut: true);
        }
        finally
        {
            KillAndDrain(process);
        }

        // Only now are both output readers at end of stream, so the snapshot is complete.
        string captured;
        lock (output)
        {
            captured = output.ToString();
        }

        return (result ?? new BootResult(process.ExitCode, string.Empty, Booted: false, TimedOut: false))
            with { Output = captured };
    }

    /// <summary>
    /// Waits for the port to accept, then proves the answer is this child's before running the
    /// scenario. Returns null when the child exited first; the caller then reports its exit code.
    /// </summary>
    private static async Task<BootResult?> ExerciseChildAsync(
        Process process, TestSite site, int port, Func<HttpClient, CancellationToken, Task>? scenario,
        CancellationToken cancellationToken)
    {
        using var client = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}"),
            Timeout = TimeSpan.FromSeconds(30)
        };
        var exited = process.WaitForExitAsync(cancellationToken);
        while (!process.HasExited)
        {
            // The startup bind runs before Kestrel binds: an accepting port means the child
            // got past it, or that another process holds the port; the probe tells them apart.
            if (!await TcpAcceptsAsync(port, cancellationToken))
            {
                await Task.Delay(100, cancellationToken);
                continue;
            }

            // Race the probe against the child's exit: if another process took the port and
            // accepts on it, the child dies on its bind while the probe waits on the other
            // process, and that exit must be reported rather than a probe timeout.
            using var probe = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var health = ProbeHealthAsync(client, site, probe.Token);
            if (await Task.WhenAny(health, exited) == exited)
            {
                await exited;
                await probe.CancelAsync();
                await ObserveAbandonedProbeAsync(health);
                return null;
            }

            var first = await health;
            if (!first.FromChild)
            {
                // Another process answered. The child normally dies on its bind: report that exit
                // so a lost port race is classified and retried like any other. A child that stays
                // up while something else answers is returned as not answering, which fails.
                if (await Task.WhenAny(exited, Task.Delay(TimeSpan.FromSeconds(10), cancellationToken)) == exited)
                {
                    await exited;
                    return null;
                }

                return new BootResult(
                    ExitCode: null, Output: string.Empty, Booted: true, TimedOut: false,
                    HealthStatus: first.Status, HealthStatusValue: first.StatusValue);
            }

            if (scenario is not null)
            {
                await scenario(client, cancellationToken);
            }

            var after = await ProbeHealthAsync(client, site, cancellationToken);
            return new BootResult(
                ExitCode: null, Output: string.Empty, Booted: true, TimedOut: false,
                HealthStatus: first.Status, HealthStatusValue: first.StatusValue,
                AnsweredByChild: true,
                AnsweredByChildAfterScenario: after.FromChild,
                StillRunningAfterScenario: !process.HasExited);
        }

        await exited;
        return null;
    }

    /// <summary>
    /// Probes /health with a fresh correlation id and checks two things only this child can
    /// produce: the gateway's echo of that id, and the per-test identity marker it was configured
    /// with. A different gateway echoes the id too, but reports its own <c>maxSourceProxyBytes</c>.
    /// </summary>
    private static async Task<HealthProbe> ProbeHealthAsync(
        HttpClient client, TestSite site, CancellationToken cancellationToken)
    {
        var marker = "odvgateway-startup-test-" + Guid.NewGuid().ToString("N");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Add(CorrelationIdHeader, marker);
        using var response = await client.SendAsync(request, cancellationToken);
        var echoed = response.Headers.TryGetValues(CorrelationIdHeader, out var values)
            && values.SequenceEqual([marker], StringComparer.Ordinal);

        string? statusValue = null;
        long? reportedMarker = null;
        try
        {
            var payload = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken)) as JsonObject;
            statusValue = (payload?["status"] as JsonValue)?.TryGetValue<string>(out var status) == true ? status : null;
            reportedMarker = (payload?["maxSourceProxyBytes"] as JsonValue)?.TryGetValue<long>(out var value) == true ? value : null;
        }
        catch (JsonException)
        {
        }

        return new HealthProbe(response.StatusCode, statusValue, FromChild: echoed && reportedMarker == site.IdentityMarker);
    }

    private static int GetFreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<bool> TcpAcceptsAsync(int port, CancellationToken parentToken)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
        attempt.CancelAfter(TimeSpan.FromMilliseconds(200));
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port, attempt.Token);
            return true;
        }
        catch (OperationCanceledException) when (!parentToken.IsCancellationRequested)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static async Task ObserveAbandonedProbeAsync(Task<HealthProbe> health)
    {
        try
        {
            await health;
        }
        catch (Exception exception) when (exception is OperationCanceledException or HttpRequestException)
        {
        }
    }

    /// <summary>
    /// Kills the child if it still runs and waits for it. The timed wait does not wait for the
    /// asynchronous stdout/stderr readers; once it succeeds, the parameterless wait does, so the
    /// captured output is complete before anyone reads it.
    /// </summary>
    private static void KillAndDrain(Process process)
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
            if (process.WaitForExit(TimeSpan.FromSeconds(10)))
            {
                process.WaitForExit();
            }
        }
        catch (InvalidOperationException)
        {
        }
    }
}
