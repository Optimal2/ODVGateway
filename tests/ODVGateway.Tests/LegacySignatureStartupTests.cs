using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace ODVGateway.Tests;

/// <summary>
/// True process-boot proof that a site upgrading with a legacy <c>ODVGateway:signatures</c> block
/// still in its own <c>appsettings.json</c> starts. WebApplicationFactory test configuration never
/// reaches the eager startup options bind in Program.cs (it only feeds request-time options, which
/// <see cref="LegacySignatureConfigurationTests"/> covers), so this suite boots the real
/// ODVGateway.dll in a child process whose content root holds the shipped appsettings.json plus the
/// legacy block, and asserts the host stays up and answers HTTP. A strict startup bind
/// (<c>ErrorOnUnknownConfiguration</c>) makes the child exit before Kestrel binds, failing here.
/// </summary>
public sealed class LegacySignatureStartupTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Startup_WithLegacySignaturesBlockInAppSettings_StaysUpAndAnswersHttp(bool enabled)
    {
        var contentRoot = Path.Join(Path.GetTempPath(), "odvgateway-startup-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contentRoot);
        try
        {
            await WriteUpgradedSiteAppSettingsAsync(contentRoot, enabled);

            var boot = await BootGatewayOnFreeLoopbackPortAsync(contentRoot);

            Assert.False(boot.TimedOut, "The gateway child neither started nor exited. Output:\n" + boot.Output);
            Assert.True(boot.Booted, $"The gateway exited during startup (exit code {boot.ExitCode}). Output:\n" + boot.Output);
            Assert.NotNull(boot.HealthStatus);
            Assert.True(boot.StillRunningAfterResponse, "The gateway exited after answering HTTP. Output:\n" + boot.Output);
        }
        finally
        {
            Directory.Delete(contentRoot, recursive: true);
        }
    }

    /// <summary>
    /// Writes the appsettings.json an upgraded site keeps: the shipped file with the
    /// <c>signatures</c> block that pre-removal releases shipped, plus sub-keys the removed
    /// startup validation used to refuse (a worker test mode without its gate, a worker memory cap
    /// below the file budget) and one key no release ever knew. The two directory keys are written
    /// as explicit JSON nulls, as a shipped file carries them, and read back to prove they are present.
    /// </summary>
    private static async Task WriteUpgradedSiteAppSettingsAsync(string contentRoot, bool enabled)
    {
        var shipped = await File.ReadAllTextAsync(
            Path.Join(AppContext.BaseDirectory, "appsettings.json"), TestContext.Current.CancellationToken);
        var root = JsonNode.Parse(shipped)!.AsObject();
        root["ODVGateway"]!.AsObject()["signatures"] = new JsonObject
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
        var path = Path.Join(contentRoot, "appsettings.json");
        await File.WriteAllTextAsync(path, root.ToJsonString(), TestContext.Current.CancellationToken);

        var written = JsonNode.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken))!;
        var signatures = written["ODVGateway"]!["signatures"]!.AsObject();
        foreach (var nullKey in new[] { "extraAnchorsDirectory", "crlDirectory" })
        {
            Assert.True(signatures.TryGetPropertyValue(nullKey, out var value), $"'{nullKey}' is missing from the written file.");
            Assert.Null(value);
        }
    }

    private sealed record BootResult(
        int ExitCode, string Output, bool Booted, bool TimedOut, HttpStatusCode? HealthStatus, bool StillRunningAfterResponse);

    private const int MaxPortAttempts = 3;

    /// <summary>
    /// Boots the child on a port that was free a moment ago. Another process can take the port
    /// between <see cref="GetFreeLoopbackPort"/> and the child's bind; only that lost race, recognised
    /// by Kestrel's address-in-use failure for exactly this port, is retried on a fresh port. Any other
    /// result, including every other startup crash, is returned at once for the caller to assert on.
    /// </summary>
    private static async Task<BootResult> BootGatewayOnFreeLoopbackPortAsync(string contentRoot)
    {
        for (var attempt = 1; ; attempt++)
        {
            var port = GetFreeLoopbackPort();
            var boot = await BootGatewayAsync(contentRoot, port);
            if (attempt == MaxPortAttempts || !IsLostPortRace(boot, port))
            {
                return boot;
            }
        }
    }

    private static bool IsLostPortRace(BootResult boot, int port)
        => !boot.Booted
            && !boot.TimedOut
            && boot.ExitCode != 0
            && boot.Output.Contains(
                $"Failed to bind to address http://127.0.0.1:{port}: address already in use.", StringComparison.Ordinal)
            && boot.Output.Contains("AddressInUseException", StringComparison.Ordinal);

    private static async Task<BootResult> BootGatewayAsync(string contentRoot, int port)
    {
        using var process = new Process();
        process.StartInfo.FileName = "dotnet";
        process.StartInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "ODVGateway.dll"));
        process.StartInfo.ArgumentList.Add("--urls");
        process.StartInfo.ArgumentList.Add($"http://127.0.0.1:{port}");
        process.StartInfo.ArgumentList.Add("--contentRoot");
        process.StartInfo.ArgumentList.Add(contentRoot);
        process.StartInfo.WorkingDirectory = contentRoot;
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

        string Snapshot()
        {
            lock (output)
            {
                return output.ToString();
            }
        }

        process.OutputDataReceived += (_, e) => Append(e.Data);
        process.ErrorDataReceived += (_, e) => Append(e.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken, timeout.Token);
        try
        {
            while (!process.HasExited)
            {
                // The startup bind runs before Kestrel binds: an accepting port means the child
                // got past it. The HTTP answer then proves the host is serving, not just listening.
                if (await TcpAcceptsAsync(port, linked.Token))
                {
                    // Race the probe against the child's exit: if another process took the port and
                    // accepts on it, the child dies on its bind while the probe waits on the other
                    // process, and that exit must be reported rather than a probe timeout.
                    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                    using var probe = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                    var health = client.GetAsync($"http://127.0.0.1:{port}/health", probe.Token);
                    var exited = process.WaitForExitAsync(linked.Token);
                    if (await Task.WhenAny(health, exited) == exited)
                    {
                        await exited;
                        await probe.CancelAsync();
                        await ObserveAbandonedProbeAsync(health);
                        break;
                    }

                    using var response = await health;
                    var stillRunning = !process.HasExited;
                    return new BootResult(
                        ExitCode: -1, Output: Snapshot(), Booted: true, TimedOut: false,
                        HealthStatus: response.StatusCode, StillRunningAfterResponse: stillRunning);
                }

                await Task.Delay(100, linked.Token);
            }
        }
        catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
        {
            return new BootResult(
                ExitCode: -1, Output: Snapshot(), Booted: false, TimedOut: true,
                HealthStatus: null, StillRunningAfterResponse: false);
        }
        finally
        {
            Kill(process);
        }

        return new BootResult(
            ExitCode: process.ExitCode, Output: Snapshot(), Booted: false, TimedOut: false,
            HealthStatus: null, StillRunningAfterResponse: false);
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

    private static async Task ObserveAbandonedProbeAsync(Task<HttpResponseMessage> health)
    {
        try
        {
            (await health).Dispose();
        }
        catch (Exception exception) when (exception is OperationCanceledException or HttpRequestException)
        {
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
            process.WaitForExit(TimeSpan.FromSeconds(10));
        }
        catch (InvalidOperationException)
        {
        }
    }
}
