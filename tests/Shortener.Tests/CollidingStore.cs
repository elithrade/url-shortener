namespace Shortener.Tests;

internal sealed class CollidingStore(int failedAttempts) : IUrlStore
{
    public int Attempts;

    private readonly Dictionary<string, string> _items = [];
    private readonly Lock _lock = new();

    public ValueTask<string?> GetAsync(string code, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_items.TryGetValue(code, out string? url))
            {
                return ValueTask.FromResult<string?>(url);
            }
        }

        return ValueTask.FromResult<string?>(null);
    }

    public ValueTask<bool> TryAddAsync(string code, string url, CancellationToken ct)
    {
        var attempts = Interlocked.Increment(ref Attempts);

        if (attempts <= failedAttempts)
        {
            return ValueTask.FromResult(false);
        }

        bool added;
        lock (_lock)
        {
            added = _items.TryAdd(code, url);
        }

        return ValueTask.FromResult(added);
    }

    public static CollidingStore Always() => new(int.MaxValue);

    public static CollidingStore Never() => new(0);
}
