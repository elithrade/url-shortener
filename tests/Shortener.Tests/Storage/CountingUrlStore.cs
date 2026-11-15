using System.Collections.Concurrent;

namespace Shortener.Tests;

// Passes every call to the inner store, counts the reads and records the codes asked for, so
// a test can prove that a cache hit never reached the store. (A decorator, like CachedUrlStore
// itself.)
internal sealed class CountingUrlStore(IUrlStore inner) : IUrlStore
{
    private readonly ConcurrentQueue<string> _codes = new();
    private int _gets;

    public int Gets => Volatile.Read(ref _gets);

    // every code passed to GetAsync, repeats included
    public IReadOnlyCollection<string> Codes => _codes;

    public ValueTask<LinkEntry?> GetAsync(string code, CancellationToken ct)
    {
        Interlocked.Increment(ref _gets);
        _codes.Enqueue(code);
        return inner.GetAsync(code, ct);
    }

    public ValueTask<bool> TryAddAsync(string code, LinkEntry linkEntry, CancellationToken ct) =>
        inner.TryAddAsync(code, linkEntry, ct);
}
