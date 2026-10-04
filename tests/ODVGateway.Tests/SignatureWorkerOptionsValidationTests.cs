using ODVGateway.Options;
using ODVGateway.Services.Signatures;

namespace ODVGateway.Tests;

/// <summary>
/// Fail-fast startup validation for the isolated-worker knobs: a test mode left in production
/// configuration and a memory cap below the file budget are refused with a named reason instead
/// of failing per request (a self-DoS) or killing healthy large files.
/// </summary>
public sealed class SignatureWorkerOptionsValidationTests
{
    private const long Transport64MiB = 64L * 1024L * 1024L;

    [Fact]
    public void WorkerTestMode_WithoutTestGate_FailsFastNamingTheKnob()
    {
        var options = new SignatureValidationOptions
        {
            WorkerTestMode = SignatureWorkerProtocol.TestModes.Hang
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            options.ValidateWorkerConfiguration(Transport64MiB, workerTestAllowed: false));

        Assert.Contains("WorkerTestMode", exception.Message, StringComparison.Ordinal);
        // The message names the same gate variable the worker enforces, so the two cannot drift.
        Assert.Contains(
            SignatureWorkerProtocol.TestEnvironmentVariable, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkerTestMode_WithTestGate_Passes()
    {
        var options = new SignatureValidationOptions
        {
            WorkerTestMode = SignatureWorkerProtocol.TestModes.Hang
        };

        options.ValidateWorkerConfiguration(Transport64MiB, workerTestAllowed: true);
    }

    [Fact]
    public void WorkerTestMode_Unset_PassesWithoutGate()
    {
        new SignatureValidationOptions().ValidateWorkerConfiguration(Transport64MiB, workerTestAllowed: false);
    }

    [Fact]
    public void WorkerTestMode_Empty_PassesWithoutGate()
    {
        // An empty value binds from an explicitly cleared setting; the worker reads it as unset
        // (EmptyToNull), so startup does the same.
        var options = new SignatureValidationOptions { WorkerTestMode = string.Empty };

        options.ValidateWorkerConfiguration(Transport64MiB, workerTestAllowed: false);
    }

    [Fact]
    public void WorkerTestMode_WhitespaceWithoutGate_IsRefused()
    {
        // Any non-empty value reaches the worker, which only honors the empty-to-null rule.
        var options = new SignatureValidationOptions { WorkerTestMode = " " };

        Assert.Throws<InvalidOperationException>(() =>
            options.ValidateWorkerConfiguration(Transport64MiB, workerTestAllowed: false));
    }

    [Fact]
    public void WorkerMaxMemoryBytes_BelowThreeTimesFileBudget_FailsFast()
    {
        var options = new SignatureValidationOptions
        {
            WorkerMaxMemoryBytes = 128L * 1024L * 1024L
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            options.ValidateWorkerConfiguration(Transport64MiB, workerTestAllowed: false));

        Assert.Contains("WorkerMaxMemoryBytes", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkerMaxMemoryBytes_AtThreeTimesFileBudget_Passes()
    {
        var options = new SignatureValidationOptions
        {
            WorkerMaxMemoryBytes = 3 * Transport64MiB
        };

        options.ValidateWorkerConfiguration(Transport64MiB, workerTestAllowed: false);
    }

    [Fact]
    public void WorkerMaxMemoryBytes_RespectsConfiguredMaxFileBytes()
    {
        // A deployment that validates small files may run a proportionally smaller worker cap.
        var options = new SignatureValidationOptions
        {
            MaxFileBytes = 32L * 1024L * 1024L,
            WorkerMaxMemoryBytes = 96L * 1024L * 1024L
        };

        options.ValidateWorkerConfiguration(Transport64MiB, workerTestAllowed: false);
    }

    [Fact]
    public void WorkerMaxMemoryBytes_ConfiguredMaxFileBytesIsCappedByTransport()
    {
        // A configured budget above the transport limit cannot raise it: the file can never be
        // larger than what the source reader carries, so the transport limit sizes the rule.
        var options = new SignatureValidationOptions
        {
            MaxFileBytes = 256L * 1024L * 1024L,
            WorkerMaxMemoryBytes = 128L * 1024L * 1024L
        };

        Assert.Throws<InvalidOperationException>(() =>
            options.ValidateWorkerConfiguration(Transport64MiB, workerTestAllowed: false));
    }

    [Theory]
    [InlineData(0, Transport64MiB)]
    [InlineData(32L * 1024L * 1024L, 32L * 1024L * 1024L)]
    [InlineData(256L * 1024L * 1024L, Transport64MiB)]
    public void GetEffectiveMaxFileBytes_FollowsTransportUnlessLowered(
        long configured, long expected)
    {
        var options = new SignatureValidationOptions { MaxFileBytes = configured };

        Assert.Equal(expected, options.GetEffectiveMaxFileBytes(Transport64MiB));
    }
}
