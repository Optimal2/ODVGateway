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

            var boot = await BootGatewayAsync(contentRoot);

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
    /// below the file budget) and one key no release ever knew.
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
        await File.WriteAllTextAsync(
            Path.Join(contentRoot, "appsettings.json"), root.ToJsonString(), TestContext.Current.CancellationToken);
    }

    private sealed record BootResult(
        int ExitCode, string Output, bool Booted, bool TimedOut, HttpStatusCode? HealthStatus, bool StillRunningAfterResponse);

    private static async Task<BootResult> BootGatewayAsync(string contentRoot)
    {
        var port = GetFreeLoopbackPort();
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
                    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                    using var response = await client.GetAsync($"http://127.0.0.1:{port}/health", linked.Token);
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
