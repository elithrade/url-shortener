using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Shortener.Tests;

public class ClickFlushServiceTests : IAsyncLifetime
{
    private ClickFlushService? _service;

    [Fact]
    public async Task Flush_sums_clicks_with_the_same_code_and_day()
    {
        var batch = await FlushOnTickAsync(
            [
                new Click("abc", At(2026, 1, 1, 0, 0, 1)),
                new Click("abc", At(2026, 1, 1, 0, 0, 1)),
                new Click("abc", At(2026, 1, 1, 0, 0, 1)),
            ],
            maxBatchSize: 2,
            flushIntervalSeconds: 10,
            advanceTimeSeconds: 10
        );

        var expected = new DailyClicks("abc", new DateOnly(2026, 1, 1), 3);
        Assert.Equal([expected], batch);
    }

    [Fact]
    public async Task Flush_splits_clicks_with_the_same_code_but_different_day()
    {
        var batch = await FlushOnTickAsync(
            [
                new Click("abc", At(2026, 1, 1, 23, 59, 59)),
                new Click("abc", At(2026, 1, 2, 0, 0, 1)),
            ],
            maxBatchSize: 100,
            flushIntervalSeconds: 10,
            advanceTimeSeconds: 10
        );

        Assert.Equivalent(
            new[]
            {
                new DailyClicks("abc", new(2026, 1, 1), 1),
                new DailyClicks("abc", new(2026, 1, 2), 1),
            },
            batch,
            strict: true
        );
    }

    [Fact]
    public async Task Flush_on_max_batch_size_without_tick()
    {
        var store = new SpyClickStore();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero));
        var (service, writer) = CreateService(
            store,
            clock,
            maxBatchSize: 2,
            flushIntervalSeconds: 10
        );

        await service.StartAsync(CancellationToken.None);

        writer.TryWrite(new Click("abc", At(2026, 1, 2, 0, 0, 1)));
        writer.TryWrite(new Click("def", At(2026, 1, 2, 0, 0, 2)));
        writer.TryWrite(new Click("ghi", At(2026, 1, 2, 0, 0, 3)));

        var firstBatch = await store.NextBatchAsync(); // size triggered a flush

        Assert.Equivalent(
            new[]
            {
                new DailyClicks("abc", new(2026, 1, 2), 1),
                new DailyClicks("def", new(2026, 1, 2), 1),
            },
            firstBatch,
            strict: true
        );

        var secondBatch = await clock.AdvanceUntilAsync(
            TimeSpan.FromSeconds(10),
            () => store.TryNextBatchAsync(TimeSpan.FromMilliseconds(500))
        );

        Assert.Equivalent(
            new[] { new DailyClicks("ghi", new(2026, 1, 2), 1) },
            secondBatch,
            strict: true
        );
    }

    [Fact]
    public async Task Tick_with_nothing_collected_does_not_flush()
    {
        var store = new SpyClickStore();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero));
        var (service, writer) = CreateService(
            store,
            clock,
            maxBatchSize: 2,
            flushIntervalSeconds: 10
        );

        await service.StartAsync(CancellationToken.None);
        writer.TryWrite(new Click("abc", At(2026, 1, 2, 0, 0, 1)));

        // abc flushed
        await clock.AdvanceUntilAsync(
            TimeSpan.FromSeconds(10),
            () => store.TryNextBatchAsync(TimeSpan.FromMilliseconds(500))
        );

        clock.Advance(TimeSpan.FromSeconds(10));

        Assert.True(await store.NoBatchWithinAsync(TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public async Task A_failure_drops_the_batch_and_service_keeps_going()
    {
        var store = new SpyClickStore();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero));
        var (service, writer) = CreateService(
            store,
            clock,
            maxBatchSize: 2,
            flushIntervalSeconds: 10
        );

        // turn on failed signal
        store.FailNext(1);

        await service.StartAsync(CancellationToken.None);
        writer.TryWrite(new Click("abc", At(2026, 1, 2, 0, 0, 1)));
        writer.TryWrite(new Click("abc", At(2026, 1, 2, 0, 0, 1)));
        writer.TryWrite(new Click("abc", At(2026, 1, 2, 0, 0, 1)));

        var failed = await clock.AdvanceUntilAsync(
            TimeSpan.FromSeconds(10),
            () => store.TryNextFailedBatchAsync(TimeSpan.FromMilliseconds(500))
        );
        Assert.Equivalent(
            new[] { new DailyClicks("abc", new(2026, 1, 2), 3) },
            failed,
            strict: true
        );

        writer.TryWrite(new Click("def", At(2026, 1, 2, 0, 0, 2)));

        var batch = await clock.AdvanceUntilAsync(
            TimeSpan.FromSeconds(10),
            () => store.TryNextBatchAsync(TimeSpan.FromMilliseconds(500))
        );

        // abc batch is dropped before def's flush
        Assert.Equal(3, service.Dropped);

        Assert.Equivalent(
            new[] { new DailyClicks("def", new(2026, 1, 2), 1) },
            batch,
            strict: true
        );
    }

    [Fact]
    public async Task Stop_flushes_pending_clicks()
    {
        var store = new SpyClickStore();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero));
        var (service, writer) = CreateService(
            store,
            clock,
            maxBatchSize: 2,
            flushIntervalSeconds: 10
        );

        await service.StartAsync(CancellationToken.None);
        writer.TryWrite(new Click("xyz", At(2026, 1, 2, 0, 0, 0)));
        await clock.AdvanceUntilAsync(
            TimeSpan.FromSeconds(10),
            () => store.TryNextBatchAsync(TimeSpan.FromMilliseconds(500))
        );

        writer.TryWrite(new Click("abc", At(2026, 1, 2, 0, 0, 1)));

        using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await service.StopAsync(stopTimeout.Token);

        var batch = await store.NextBatchAsync();

        Assert.Equivalent(
            new[] { new DailyClicks("abc", new(2026, 1, 2), 1) },
            batch,
            strict: true
        );
    }

    private static DateTimeOffset At(
        int year,
        int month,
        int day,
        int hour,
        int minute,
        int second
    ) => new(year, month, day, hour, minute, second, TimeSpan.Zero);

    private async Task<IReadOnlyCollection<DailyClicks>> FlushOnTickAsync(
        Click[] clicks,
        int maxBatchSize,
        int flushIntervalSeconds,
        int advanceTimeSeconds
    )
    {
        var store = new SpyClickStore();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var (service, writer) = CreateService(
            store,
            clock,
            maxBatchSize: maxBatchSize,
            flushIntervalSeconds: flushIntervalSeconds
        );

        await service.StartAsync(CancellationToken.None);

        foreach (var click in clicks)
        {
            writer.TryWrite(click);
        }

        return await clock.AdvanceUntilAsync(
            TimeSpan.FromSeconds(advanceTimeSeconds),
            () => store.TryNextBatchAsync(TimeSpan.FromMilliseconds(500))
        );
    }

    private (ClickFlushService, ChannelWriter<Click>) CreateService(
        IClickStore store,
        FakeTimeProvider clock,
        int maxBatchSize,
        int flushIntervalSeconds
    )
    {
        var channel = Channel.CreateUnbounded<Click>();
        var service = new ClickFlushService(
            channel.Reader,
            store,
            clock,
            Options.Create(
                new ClickOptions
                {
                    MaxBatchSize = maxBatchSize,
                    FlushIntervalSeconds = flushIntervalSeconds,
                }
            ),
            NullLogger<ClickFlushService>.Instance
        );

        _service = service;

        return (service, channel.Writer);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_service is not null)
        {
            await _service.StopAsync(CancellationToken.None);
        }
    }
}
