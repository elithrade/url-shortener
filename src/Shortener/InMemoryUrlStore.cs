namespace Shortener;

public sealed class InMemoryUrlStore : IUrlStore
{
    private readonly Dictionary<string, string> _store = [];
    private readonly Lock _lock = new();

    public ValueTask<string?> GetAsync(string code, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // lock (_lock)
        {
            if (_store.TryGetValue(code, out string? url))
            {
                return ValueTask.FromResult<string?>(url);
            }
        }

        return ValueTask.FromResult<string?>(null);
    }

    public ValueTask<bool> TryAddAsync(string code, string url, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        bool added;
        // lock (_lock)
        {
            added = _store.TryAdd(code, url);
        }

        return ValueTask.FromResult(added);
    }
}
