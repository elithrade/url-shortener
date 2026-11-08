using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace Shortener;

public sealed partial class ClickFlushService(
    ChannelReader<Click> channelReader,
    IClickStore store,
    TimeProvider clock,
    IOptions<ClickOptions> options,
    ILogger<ClickFlushService> logger
) : BackgroundService
{
    private readonly Dictionary<(string Code, DateOnly DateOnly), int> _entries = [];
    private readonly int _maxBatchSize = options.Value.MaxBatchSize;
    private long _lostClicks;

    // clicks in batches that failed to flush and were dropped
    public long Dropped => Interlocked.Read(ref _lostClicks);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using PeriodicTimer timer = new(
            TimeSpan.FromSeconds(options.Value.FlushIntervalSeconds),
            clock
        );

        // wait for timer tick
        var tick = timer.WaitForNextTickAsync(ct).AsTask();
        // wait for something to read
        var read = channelReader.WaitToReadAsync(ct).AsTask();

        try
        {
            // if not cancelled, keep running forever
            while (!ct.IsCancellationRequested)
            {
                await Task.WhenAny([tick, read]);
                // one of the tasks is done
                // we are ready to read all clicks
                TakeQueued();
                if (read.IsCompleted)
                {
                    // keep waiting for more
                    read = channelReader.WaitToReadAsync(ct).AsTask();
                }

                // timer ticked, flush all
                var ticked = tick.IsCompletedSuccessfully;
                if (ticked || _entries.Count >= _maxBatchSize)
                {
                    await FlushAsync(ct);
                }

                if (ticked)
                {
                    // keep waiting for the next tick
                    tick = timer.WaitForNextTickAsync(ct).AsTask();
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // normal shutdown, not error
        }

        // Cancel signalled, start draining
        using var drainTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        do
        {
            TakeQueued();
            await FlushAsync(drainTimeout.Token);
        } while (channelReader.Count > 0);
    }

    private void TakeQueued()
    {
        var queued = channelReader.Count;
        var taken = 0;
        while (
            taken < queued && _entries.Count < _maxBatchSize && channelReader.TryRead(out var click)
        )
        {
            // move to local dictionary
            var key = (click.Code, DateOnly.FromDateTime(click.At.UtcDateTime));
            if (_entries.ContainsKey(key))
            {
                _entries[key] += 1;
            }
            else
            {
                _entries[key] = 1;
            }
            taken++;
        }
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        if (_entries.Count == 0)
        {
            return;
        }

        var dailyClicks = _entries
            .Select(kvp => new DailyClicks(kvp.Key.Code, kvp.Key.DateOnly, kvp.Value))
            .ToArray();

        try
        {
            await store.AddAsync(dailyClicks, ct);
        }
        catch (StorageUnavailableException ex)
        {
            var total = _entries.Sum(kvp => kvp.Value);
            Interlocked.Add(ref _lostClicks, total);

            LogFlushFailed(ex, total);
        }
        finally
        {
            _entries.Clear();
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Flush failed, dropped {Count} clicks")]
    private partial void LogFlushFailed(Exception exception, long count);
}
