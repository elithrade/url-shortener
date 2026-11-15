namespace Shortener.Tests;

// Fails the first `failures` lookups with StorageUnavailableException, then answers "no such
// link". With int.MaxValue it is a database that stays down. It fails at once and ignores the
// token, like a refused connection.
internal sealed class FlakyUrlStore(int failures) : IUrlStore
{
    private readonly SemaphoreSlim _called = new(0);
    private int _gets;

    // every lookup made, failed ones included
    public int Gets => Volatile.Read(ref _gets);

    public ValueTask<LinkEntry?> GetAsync(string code, CancellationToken ct)
    {
        var call = Interlocked.Increment(ref _gets);
        _called.Release();
        if (call <= failures)
        {
            throw new StorageUnavailableException("the store is down", new TimeoutException());
        }
        return ValueTask.FromResult<LinkEntry?>(null);
    }

    public ValueTask<bool> TryAddAsync(string code, LinkEntry linkEntry, CancellationToken ct) =>
        throw new NotSupportedException("warm-up only reads");

    // waits until `count` more lookups have been made since the last wait
    public async Task WaitForCallsAsync(int count, TimeSpan? timeout = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(5);
        for (var i = 0; i < count; i++)
        {
            if (!await _called.WaitAsync(limit))
            {
                throw new TimeoutException($"only {i} of {count} lookups were made");
            }
        }
    }
}
