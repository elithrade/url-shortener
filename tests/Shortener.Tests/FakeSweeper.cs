namespace Shortener.Tests;

internal sealed class FakeSweeper : IExpirySweeper
{
    private readonly SemaphoreSlim _called = new(0);

    public Func<int, bool>? ThrowOnCall;

    public int Calls;

    public int Sweep()
    {
        var call = Interlocked.Increment(ref Calls);
        _called.Release();

        if (ThrowOnCall?.Invoke(call) == true)
            throw new InvalidOperationException("Sweep failed");

        return 0;
    }

    public Task<bool> WaitForCallAsync(TimeSpan? timeout = null) =>
        _called.WaitAsync(timeout ?? TimeSpan.FromSeconds(5));
}
