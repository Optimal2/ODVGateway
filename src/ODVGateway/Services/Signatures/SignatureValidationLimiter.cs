using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;
using ODVGateway.Options;

namespace ODVGateway.Services.Signatures;

/// <summary>Bounds buffering and validation together; saturated requests never queue PDF bodies.</summary>
public sealed class SignatureValidationLimiter(IOptions<ODVGatewayOptions> options) : IDisposable
{
    private readonly ConcurrencyLimiter limiter = new(new ConcurrencyLimiterOptions
    {
        PermitLimit = Math.Clamp(options.Value.Signatures.MaxConcurrentValidations, 1, 16),
        QueueLimit = 0
    });

    public RateLimitLease TryAcquire() => limiter.AttemptAcquire();

    public void Dispose() => limiter.Dispose();
}
