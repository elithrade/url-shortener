namespace Shortener.Tests;

// Passes every call to the inner store and counts the reads, so a test can prove that a cache
// hit never reached the store. (A decorator, like CachedUrlStore itself.)
internal sealed class CountingUrlStore(IUrlStore inner) : IUrlStore
{
    private int _gets;

    public int Gets => Volatile.Read(ref _gets);

    public ValueTask<LinkEntry?> GetAsync(string code, CancellationToken ct)
    {
        Interlocked.Increment(ref _gets);
        return inner.GetAsync(code, ct);
    }

    public ValueTask<bool> TryAddAsync(string code, LinkEntry linkEntry, CancellationToken ct) =>
        inner.TryAddAsync(code, linkEntry, ct);
}
