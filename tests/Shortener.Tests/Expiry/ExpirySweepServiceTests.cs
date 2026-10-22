namespace Shortener.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

public class ExpirySweepServiceTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    private static (
        FakeTimeProvider Clock,
        FakeSweeper Sweeper,
        ExpirySweepService Service
    ) Create()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var sweeper = new FakeSweeper();

        return (
            clock,
            sweeper,
            new ExpirySweepService(sweeper, clock, NullLogger<ExpirySweepService>.Instance)
        );
    }

    private static async Task AdvanceUntilFirstSweepAsync(
        FakeTimeProvider clock,
        FakeSweeper sweeper
    )
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            clock.Advance(Interval);
            if (await sweeper.WaitForCallAsync(TimeSpan.FromMilliseconds(500)))
            {
                return;
            }
        }
        throw new TimeoutException("The service never swept");
    }

    [Fact]
    public async Task Sweep_once_per_interval_not_before()
    {
        var (clock, sweeper, service) = Create();
        await service.StartAsync(CancellationToken.None);

        try
        {
            await AdvanceUntilFirstSweepAsync(clock, sweeper);

            for (var round = 1; round <= 3; round++)
            {
                clock.Advance(Interval - TimeSpan.FromSeconds(1));
                // 59s, too early
                Assert.False(await sweeper.WaitForCallAsync(TimeSpan.FromMilliseconds(100)));

                clock.Advance(TimeSpan.FromSeconds(1));
                Assert.True(await sweeper.WaitForCallAsync());
            }
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Sweep_continues_after_exception_thrown()
    {
        var (clock, sweeper, service) = Create();

        // first call should throw
        sweeper.ThrowOnCall = call => call == 1;

        await service.StartAsync(CancellationToken.None);

        try
        {
            // the first sweep throws; this waits until it has happened
            await AdvanceUntilFirstSweepAsync(clock, sweeper);
            Assert.Equal(1, sweeper.Calls);

            clock.Advance(Interval);
            Assert.True(await sweeper.WaitForCallAsync());
            Assert.Equal(2, sweeper.Calls);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Service_stops_cleanly()
    {
        var (clock, sweeper, service) = Create();

        await service.StartAsync(CancellationToken.None);
        await AdvanceUntilFirstSweepAsync(clock, sweeper);

        using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await service.StopAsync(stopTimeout.Token);

        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);

        // nothing sweeps after service stopped
        clock.Advance(Interval);
        Assert.False(await sweeper.WaitForCallAsync(TimeSpan.FromMilliseconds(100)));
        Assert.Equal(1, sweeper.Calls);
    }
}
