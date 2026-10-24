namespace Shortener.Tests;

internal sealed class ExceptionStore(Exception ex) : IUrlStore
{
    public int Attempts;

    public ValueTask<string?> GetAsync(string code, CancellationToken ct)
    {
        throw ex;
    }

    public ValueTask<bool> TryAddAsync(string code, LinkEntry linkEntry, CancellationToken ct)
    {
        Interlocked.Increment(ref Attempts);
        throw ex;
    }
}
