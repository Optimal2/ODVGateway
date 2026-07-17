using ODVGateway.Options;
using ODVGateway.Services;

namespace ODVGateway.Tests;

public sealed class WebClientSourceProxyLimiterTests
{
    private static WebClientSourceProxyLimiter CreateLimiter(int proxyMaxConcurrency)
    {
        var options = new ODVGatewayOptions
        {
            WebClientSourceFallback = new WebClientSourceFallbackOptions
            {
                ProxyMaxConcurrency = proxyMaxConcurrency
            }
        };

        return new WebClientSourceProxyLimiter(new StaticOptionsMonitor<ODVGatewayOptions>(options));
    }

    [Fact]
    public async Task WaitAsync_LimitOne_BlocksUntilLeaseDisposed()
    {
        var limiter = CreateLimiter(1);

        var first = await limiter.WaitAsync(CancellationToken.None);
        var secondTask = limiter.WaitAsync(CancellationToken.None);

        var completedBeforeRelease = await Task.WhenAny(secondTask, Task.Delay(200)) == secondTask;
        Assert.False(completedBeforeRelease, "Second lease should block while the first is held.");

        first.Dispose();
        var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(second);
        second.Dispose();
    }

    [Fact]
    public async Task WaitAsync_LimitTwo_AllowsTwoConcurrentLeases()
    {
        var limiter = CreateLimiter(2);

        var first = await limiter.WaitAsync(CancellationToken.None);
        var second = await limiter.WaitAsync(CancellationToken.None);
        var thirdTask = limiter.WaitAsync(CancellationToken.None);

        var completedBeforeRelease = await Task.WhenAny(thirdTask, Task.Delay(200)) == thirdTask;
        Assert.False(completedBeforeRelease, "Third lease should block while two leases are held.");

        first.Dispose();
        var third = await thirdTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(third);

        second.Dispose();
        third.Dispose();
    }

    [Fact]
    public async Task WaitAsync_ZeroOrNegativeLimit_IsClampedToOne()
    {
        var limiter = CreateLimiter(0);

        var first = await limiter.WaitAsync(CancellationToken.None);
        var secondTask = limiter.WaitAsync(CancellationToken.None);

        var completedBeforeRelease = await Task.WhenAny(secondTask, Task.Delay(200)) == secondTask;
        Assert.False(completedBeforeRelease, "A zero configured limit must still behave as limit one.");

        first.Dispose();
        (await secondTask.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
    }

    [Fact]
    public async Task WaitAsync_CancelledToken_ThrowsOperationCanceledException()
    {
        var limiter = CreateLimiter(1);
        using var lease = await limiter.WaitAsync(CancellationToken.None);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => limiter.WaitAsync(cts.Token));
    }

    [Fact]
    public async Task Lease_Dispose_IsIdempotent()
    {
        var limiter = CreateLimiter(1);

        var lease = await limiter.WaitAsync(CancellationToken.None);
        lease.Dispose();
        lease.Dispose();

        // A double-dispose must not over-release the semaphore; the next
        // waiter should get exactly one slot and a second waiter must block.
        var next = await limiter.WaitAsync(CancellationToken.None);
        var blockedTask = limiter.WaitAsync(CancellationToken.None);
        var completedEarly = await Task.WhenAny(blockedTask, Task.Delay(200)) == blockedTask;
        Assert.False(completedEarly, "Double dispose must not release the semaphore twice.");

        next.Dispose();
        (await blockedTask.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
    }
}
