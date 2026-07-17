using Microsoft.Extensions.Options;

namespace ODVGateway.Tests;

// Minimal in-memory IOptionsMonitor so options-consuming services can be
// constructed in unit tests without a DI container.
internal sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
{
    public StaticOptionsMonitor(T value)
    {
        CurrentValue = value;
    }

    public T CurrentValue { get; }

    public T Get(string? name)
    {
        return CurrentValue;
    }

    public IDisposable? OnChange(Action<T, string?> listener)
    {
        return null;
    }
}
