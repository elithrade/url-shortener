namespace Shortener.Tests;

internal sealed class AlwaysCollidingStore : IUrlStore
{
    public int Attempts;

    public ValueTask<string?> GetAsync(string code, CancellationToken ct)
    {
        return ValueTask.FromResult<string?>(null);
    }

    public ValueTask<bool> TryAddAsync(string code, string url, CancellationToken ct)
    {
        Interlocked.Increment(ref Attempts);
        return ValueTask.FromResult(false);
    }
}
