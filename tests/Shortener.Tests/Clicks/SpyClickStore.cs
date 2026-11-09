using System.Threading.Channels;

namespace Shortener.Tests;

// Records each successful AddAsync batch so a test can await the flush that happens on the
// service's own task, instead of polling or sleeping.
public sealed class SpyClickStore : IClickStore
{
    private readonly Channel<IReadOnlyCollection<DailyClicks>> _batches = Channel.CreateUnbounded<
        IReadOnlyCollection<DailyClicks>
    >();
    private readonly Channel<IReadOnlyCollection<DailyClicks>> _failedBatches =
        Channel.CreateUnbounded<IReadOnlyCollection<DailyClicks>>();
    private int _failNext;

    // the next N calls to AddAsync throw StorageUnavailableException; their batches are recorded
    // as failed, never as successful
    public void FailNext(int calls) => Interlocked.Exchange(ref _failNext, calls);

    public ValueTask AddAsync(IReadOnlyCollection<DailyClicks> clicks, CancellationToken ct)
    {
        if (Interlocked.Decrement(ref _failNext) >= 0)
        {
            _failedBatches.Writer.TryWrite(clicks.ToArray());
            throw new StorageUnavailableException("spy failure", new TimeoutException());
        }
        Interlocked.Exchange(ref _failNext, 0);

        // copy, because the service may reuse or clear its collection after the call
        _batches.Writer.TryWrite(clicks.ToArray());
        return ValueTask.CompletedTask;
    }

    // waits for the next recorded batch; fails the test if none arrives in time
    public async Task<IReadOnlyCollection<DailyClicks>> NextBatchAsync(TimeSpan? timeout = null)
    {
        var wait = timeout ?? TimeSpan.FromSeconds(5);
        using var cts = new CancellationTokenSource(wait);
        try
        {
            return await _batches.Reader.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"No batch was flushed within {wait}");
        }
    }

    // the next recorded batch, or null if none arrives within the wait
    public async Task<IReadOnlyCollection<DailyClicks>?> TryNextBatchAsync(TimeSpan wait)
    {
        using var cts = new CancellationTokenSource(wait);
        try
        {
            return await _batches.Reader.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    // the next batch whose AddAsync failed, or null if none fails within the wait
    public async Task<IReadOnlyCollection<DailyClicks>?> TryNextFailedBatchAsync(TimeSpan wait)
    {
        using var cts = new CancellationTokenSource(wait);
        try
        {
            return await _failedBatches.Reader.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    // true if no batch arrives within the wait; can only pass wrongly, never fail wrongly
    public async Task<bool> NoBatchWithinAsync(TimeSpan wait)
    {
        await Task.Delay(wait);
        return !_batches.Reader.TryPeek(out _);
    }

    public ValueTask<long> GetAsync(
        string code,
        DateOnly start,
        DateOnly end,
        CancellationToken ct
    ) => throw new NotSupportedException();
}
