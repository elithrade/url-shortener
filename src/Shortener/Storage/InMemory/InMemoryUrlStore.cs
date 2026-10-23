namespace Shortener;

public sealed class InMemoryUrlStore(TimeProvider timeProvider) : IUrlStore, IExpirySweeper
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

    public ValueTask<int> SweepAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var now = _clock.GetUtcNow();
        var removedCount = 0;

        // removes every expired entry, returns how many it removed
        lock (_lock)
        {
            // when ExpiresAt is null, never expires now >= null is false
            var expiredEnties = _store.Where(e => now >= e.Value.ExpiresAt).ToArray();
            foreach (var expiredEntry in expiredEnties)
            {
                if (_store.Remove(expiredEntry.Key))
                {
                    removedCount += 1;
                }
            }
        }

        return ValueTask.FromResult(removedCount);
    }

    public ValueTask<bool> TryAddAsync(string code, LinkEntry linkEntry, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var now = _clock.GetUtcNow();

        if (linkEntry.ExpiresAt <= now)
        {
            throw new ArgumentOutOfRangeException(
                nameof(linkEntry),
                "ExpiresAt must be in the future"
            );
        }

        bool added;
        lock (_lock)
        {
            added = _store.TryAdd(code, linkEntry);
        }

        return ValueTask.FromResult(added);
    }
}
