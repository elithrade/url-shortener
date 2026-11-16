namespace Shortener.Tests;

// Holds every GetAsync until Release, so a test can see how many lookups run at once. Left
// closed, it stands in for a stalled database. A held call still ends when its token is
// cancelled, as Npgsql's do, so stopping the service doesn't hang the test.
internal sealed class GatedUrlStore : IUrlStore
{
    private readonly TaskCompletionSource _gate = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private readonly SemaphoreSlim _arrived = new(0);
    private int _inFlight;
    private int _maxInFlight;

    public int InFlight => Volatile.Read(ref _inFlight);

    // the most calls that were inside GetAsync at the same moment
    public int MaxInFlight => Volatile.Read(ref _maxInFlight);

    public async ValueTask<LinkEntry?> GetAsync(string code, CancellationToken ct)
    {
        RaiseMax(Interlocked.Increment(ref _inFlight));
        _arrived.Release();
        try
        {
            await _gate.Task.WaitAsync(ct);
            return null;
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    public ValueTask<bool> TryAddAsync(string code, LinkEntry linkEntry, CancellationToken ct) =>
        throw new NotSupportedException("warm-up only reads");

    // lets every held call through, and every later call passes straight through
    public void Release() => _gate.TrySetResult();

    // fails every held call with `exception`, and every later call fails at once
    public void Fail(Exception exception) => _gate.TrySetException(exception);

    // waits until `count` more calls have reached the gate since the last wait
    public async Task WaitForArrivalsAsync(int count, TimeSpan? timeout = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(5);
        for (var i = 0; i < count; i++)
        {
            if (!await _arrived.WaitAsync(limit))
            {
                throw new TimeoutException($"only {i} of {count} calls reached the gate");
            }
        }
    }

    private void RaiseMax(int value)
    {
        var seen = Volatile.Read(ref _maxInFlight);
        while (value > seen)
        {
            var previous = Interlocked.CompareExchange(ref _maxInFlight, value, seen);
            if (previous == seen)
            {
                return;
            }
            seen = previous;
        }
    }
}
