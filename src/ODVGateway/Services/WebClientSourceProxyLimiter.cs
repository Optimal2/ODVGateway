using Microsoft.Extensions.Options;
using ODVGateway.Options;

namespace ODVGateway.Services;

public sealed class WebClientSourceProxyLimiter
{
    private readonly object _gate = new();
    private readonly IOptionsMonitor<ODVGatewayOptions> _options;
    private SemaphoreSlim? _limiter;
    private int _limit;

    public WebClientSourceProxyLimiter(IOptionsMonitor<ODVGatewayOptions> options)
    {
        _options = options;
    }

    public async Task<IDisposable> WaitAsync(CancellationToken cancellationToken)
    {
        var limiter = GetLimiter();
        await limiter.WaitAsync(cancellationToken);
        return new Lease(limiter);
    }

    private SemaphoreSlim GetLimiter()
    {
        var limit = Math.Max(1, _options.CurrentValue.WebClientSourceFallback.ProxyMaxConcurrency);
        lock (_gate)
        {
            if (_limiter is not null && _limit == limit)
            {
                return _limiter;
            }

            _limit = limit;
            _limiter = new SemaphoreSlim(limit, limit);
            return _limiter;
        }
    }

    private sealed class Lease : IDisposable
    {
        private SemaphoreSlim? _limiter;

        public Lease(SemaphoreSlim limiter)
        {
            _limiter = limiter;
        }

        public void Dispose()
        {
            var limiter = Interlocked.Exchange(ref _limiter, null);
            limiter?.Release();
        }
    }
}
