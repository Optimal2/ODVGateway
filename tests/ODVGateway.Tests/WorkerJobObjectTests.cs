using System.Diagnostics;
using ODVGateway.Services.Signatures;

namespace ODVGateway.Tests;

/// <summary>
/// Argument guards and lifetime behaviour of the Windows job-object cage: null and
/// handle-less processes map to fail-fast or the monitor-only fallback, and a live worker
/// on Windows gets a usable, idempotently-disposable cage.
/// </summary>
public sealed class WorkerJobObjectTests
{
    [Fact]
    public void TryAssign_NullProcess_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => WorkerJobObject.TryAssign(null!));
    }

    [Fact]
    public void TryAssign_ProcessWithoutHandle_ReturnsNull()
    {
        using var process = new Process();
        Assert.Null(WorkerJobObject.TryAssign(process));
    }

    [Fact]
    public void TryAssign_LiveProcess_ReturnsUsableCage_DisposeIsIdempotent()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows job objects only exist on Windows.");

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c ping -n 30 127.0.0.1 >nul",
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        Assert.NotNull(process);
        try
        {
            // NotNull proves the explicit kernel32 entry points resolve: a wrong EntryPoint
            // surfaces as EntryPointNotFoundException, which TryAssign swallows into the
            // null (monitor-only) fallback — a silent regression this assertion would catch.
            var job = WorkerJobObject.TryAssign(process);
            Assert.NotNull(job);
            job.Dispose();
            job.Dispose();
        }
        finally
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
        }
    }
}
