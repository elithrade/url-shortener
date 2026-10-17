namespace Shortener;

public sealed class InMemoryUrlStore(TimeProvider timeProvider) : IUrlStore
{
    private readonly Dictionary<string, LinkEntry> _store = [];
    private readonly Lock _lock = new();
    private readonly TimeProvider _clock = timeProvider;

    public ValueTask<string?> GetAsync(string code, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        lock (_lock)
        {
            if (_store.TryGetValue(code, out LinkEntry? linkEntry))
            {
                if (linkEntry.ExpiresAt is not null && _clock.GetUtcNow() >= linkEntry.ExpiresAt)
                {
                    return ValueTask.FromResult<string?>(null);
                }

                return ValueTask.FromResult<string?>(linkEntry.Url);
            }
        }

        return ValueTask.FromResult<string?>(null);
    }

    public ValueTask<bool> TryAddAsync(string code, LinkEntry linkEntry, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        bool added;
        lock (_lock)
        {
            added = _store.TryAdd(code, linkEntry);
        }

        return ValueTask.FromResult(added);
    }
}
