using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ODVGateway.Services.Signatures;

namespace ODVGateway.Tests;

/// <summary>
/// True process-boot startup validation for the isolated-worker knobs. WebApplicationFactory test
/// configuration never reaches Program.cs startup options (it only feeds request-time options),
/// so these tests boot the real ODVGateway.dll in a child process with its own environment block
/// and assert it fails fast with the named reason. The vehicle is the same <c>dotnet</c> host the
/// isolation tests already use to spawn workers.
/// </summary>
public sealed class SignatureStartupTests
{
    [Fact]
    public async Task Startup_WorkerTestModeWithoutGate_FailsFastNamingTheKnob()
    {
        var boot = await BootGatewayAsync(new Dictionary<string, string?>
        {
            // Absent gate with a configured test mode: the production misconfiguration that used
            // to fail every request instead of failing the start.
            [SignatureWorkerProtocol.TestEnvironmentVariable] = null,
            ["ODVGateway__signatures__workerTestMode"] = SignatureWorkerProtocol.TestModes.Hang
        });

        Assert.False(boot.TimedOut, "The gateway child neither failed fast nor started. Output:\n" + boot.Output);
        Assert.False(boot.Booted, "The gateway booted past startup validation. Output:\n" + boot.Output);
        Assert.NotEqual(0, boot.ExitCode);
        Assert.Contains("WorkerTestMode", boot.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Startup_WorkerMemoryCapBelowFileBudget_FailsFast()
    {
        var boot = await BootGatewayAsync(new Dictionary<string, string?>
        {
            ["ODVGateway__signatures__workerMaxMemoryBytes"] = (128L * 1024L * 1024L).ToString()
        });

        Assert.False(boot.TimedOut, "The gateway child neither failed fast nor started. Output:\n" + boot.Output);
        Assert.False(boot.Booted, "The gateway booted past startup validation. Output:\n" + boot.Output);
        Assert.NotEqual(0, boot.ExitCode);
        Assert.Contains("WorkerMaxMemoryBytes", boot.Output, StringComparison.Ordinal);
    }

    private sealed record BootResult(int ExitCode, string Output, bool Booted, bool TimedOut);

    /// <summary>
    /// Boots the gateway once. A <c>null</c> environment value removes the variable from the
    /// child's environment block (the test process itself carries the worker test gate).
    /// </summary>
    private static async Task<BootResult> BootGatewayAsync(Dictionary<string, string?> environment)
    {
        var process = new Process();
        process.StartInfo.FileName = "dotnet";
        process.StartInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "ODVGateway.dll"));
        process.StartInfo.ArgumentList.Add("--urls");
        process.StartInfo.ArgumentList.Add("http://127.0.0.1:5599");
        process.StartInfo.WorkingDirectory = AppContext.BaseDirectory;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        foreach (var (name, value) in environment)
        {
            if (value is null)
            {
                foreach (var key in process.StartInfo.Environment.Keys.Cast<string>().ToList())
                {
                    if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                    {
                        process.StartInfo.Environment.Remove(key);
                    }
                }
            }
            else
            {
                process.StartInfo.Environment[name] = value;
            }
        }

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

        string Snapshot()
        {
            lock (output)
            {
                return output.ToString();
            }
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken, timeout.Token);
        try
        {
            while (!process.HasExited)
            {
                // Validation throws before Kestrel binds: an accepting port means the child
                // booted past it. Log output is not used: NLog has no console target here.
                if (await TcpAcceptsAsync(5599, linked.Token))
                {
                    Kill(process);
                    return new BootResult(ExitCode: -1, Output: Snapshot(), Booted: true, TimedOut: false);
                }

                await Task.Delay(100, linked.Token);
            }
        }
        catch (OperationCanceledException) when (TestContext.Current.CancellationToken.IsCancellationRequested)
        {
            Kill(process);
            throw;
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            return new BootResult(ExitCode: -1, Output: Snapshot(), Booted: false, TimedOut: true);
        }

        try
        {
            process.WaitForExit(TimeSpan.FromSeconds(10));
        }
        catch (InvalidOperationException)
        {
        }

        return new BootResult(ExitCode: process.ExitCode, Output: Snapshot(), Booted: false, TimedOut: false);
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
            process.WaitForExit(TimeSpan.FromSeconds(5));
        }
        catch (InvalidOperationException)
        {
        }
    }
}
