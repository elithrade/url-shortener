using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Shortener.Tests;

// The warm-up service on its own, with a fake store and a FakeTimeProvider.
// Stores used: CountingUrlStore (counts lookups and records their codes), GatedUrlStore (holds
// lookups until released; left closed it is a stalled database) and FlakyUrlStore (fails the
// first N lookups; with int.MaxValue it is a database that stays down).
public class WarmUpServiceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // how long a test waits in real time before it gives up
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);

    // real time given to a broken version to do the wrong thing before the test looks
    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(50);

    private WarmUpService? _service;

    [Fact]
    public async Task Calls_the_store_the_configured_number_of_times()
    {
        var options = new WarmUpOptions
        {
            Calls = 10,
            DeadlineSeconds = 3,
            Parallelism = 3,
        };

        var store = new CountingUrlStore(new InMemoryUrlStore(TimeProvider.System));
        var (_, service) = Create(options, store);
        await service.StartAsync(CancellationToken.None);
        await service.Ready.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(10, store.Gets);
    }

    [Fact]
    public async Task Never_has_more_than_parallelism_calls_in_flight()
    {
        var store = new GatedUrlStore();
        var options = new WarmUpOptions
        {
            Calls = 10,
            DeadlineSeconds = 3,
            Parallelism = 3,
        };
        var (_, service) = Create(options, store);

        await service.StartAsync(CancellationToken.None);
        await store.WaitForArrivalsAsync(3);
        await Task.Delay(50);
        store.Release();
        await service.Ready.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(3, store.MaxInFlight);
    }

    // warm-up must never read, cache or count a click for a real link
    [Fact]
    public async Task Looks_up_a_code_that_no_link_can_have()
    {
        var store = new CountingUrlStore(new InMemoryUrlStore(TimeProvider.System));
        var (_, service) = Create(Settings(), store);

        await service.StartAsync(CancellationToken.None);
        await service.Ready.WaitAsync(Limit);

        Assert.Equal(10, store.Codes.Count);
        Assert.All(store.Codes, code => Assert.Equal(WarmUpService.Code, code));
        // CodeGenerator only uses letters and digits
        Assert.Contains(WarmUpService.Code, c => !char.IsAsciiLetterOrDigit(c));
    }

    [Fact]
    public async Task Is_ready_after_the_last_call_finishes()
    {
        var gate = new GatedUrlStore();
        var store = new CountingUrlStore(gate);
        var (_, service) = Create(Settings(), store);

        await service.StartAsync(CancellationToken.None);
        await gate.WaitForArrivalsAsync(3);
        gate.Release();
        await service.Ready.WaitAsync(Limit);

        Assert.Equal(10, store.Gets);
        Assert.Equal(0, gate.InFlight);
    }

    [Fact]
    public async Task Is_not_ready_while_calls_are_running()
    {
        var store = new GatedUrlStore();
        var (_, service) = Create(Settings(), store);

        await service.StartAsync(CancellationToken.None);
        await store.WaitForArrivalsAsync(3);
        await Task.Delay(Pause);

        Assert.False(service.Ready.IsCompleted);
    }

    [Fact]
    public async Task Zero_calls_is_ready_at_once()
    {
        var store = new CountingUrlStore(new InMemoryUrlStore(TimeProvider.System));
        var (_, service) = Create(Settings(calls: 0), store);

        await service.StartAsync(CancellationToken.None);
        await service.Ready.WaitAsync(Limit);

        Assert.Equal(0, store.Gets);
    }

    // only lookups that succeed count: 4 failures and 10 successes are 14 lookups
    [Fact]
    public async Task A_failed_call_is_retried_until_it_succeeds()
    {
        var store = new FlakyUrlStore(failures: 4);
        var logger = new FakeLogger<WarmUpService>();
        var (clock, service) = Create(Settings(), store, logger);

        await service.StartAsync(CancellationToken.None);
        // a failed lookup waits on the fake clock before its retry; 50 steps stay under the deadline
        await clock.AdvanceUntilAsync(
            TimeSpan.FromMilliseconds(100),
            async () => await Task.WhenAny(service.Ready, Task.Delay(20)) == service.Ready,
            attempts: 50
        );

        Assert.Equal(14, store.Gets);
        Assert.Empty(logger.Collector.GetSnapshot());
    }

    // the fake clock never moves here, so a retry can only happen if nothing waits for the clock
    [Fact]
    public async Task Waits_before_retrying_a_failed_call()
    {
        var store = new FlakyUrlStore(failures: int.MaxValue);
        var (_, service) = Create(Settings(), store);

        await service.StartAsync(CancellationToken.None);
        await store.WaitForCallsAsync(3);
        await Task.Delay(Pause);

        Assert.Equal(3, store.Gets);
    }

    // a database that stays down: warm-up keeps trying, then gives up at the deadline
    [Fact]
    public async Task Is_ready_at_the_deadline_when_the_store_keeps_failing()
    {
        var store = new FlakyUrlStore(failures: int.MaxValue);
        var (clock, service) = Create(Settings(deadlineSeconds: 15), store);

        await service.StartAsync(CancellationToken.None);
        // after the first lookups, the service is running and its deadline timer exists
        await store.WaitForCallsAsync(3);

        clock.Advance(TimeSpan.FromSeconds(14));
        await Task.Delay(Pause);
        Assert.False(service.Ready.IsCompleted);

        clock.Advance(TimeSpan.FromSeconds(1));
        await service.Ready.WaitAsync(Limit);
        await AssertEndedWithoutErrorAsync(service);
    }

    // a stalled database: the held lookups end when the deadline cancels them, as Npgsql's do
    [Fact]
    public async Task Is_ready_at_the_deadline_when_the_store_hangs()
    {
        var store = new GatedUrlStore();
        var (clock, service) = Create(Settings(deadlineSeconds: 15), store);

        await service.StartAsync(CancellationToken.None);
        await store.WaitForArrivalsAsync(3);

        clock.Advance(TimeSpan.FromSeconds(14));
        await Task.Delay(Pause);
        Assert.False(service.Ready.IsCompleted);

        clock.Advance(TimeSpan.FromSeconds(1));
        await service.Ready.WaitAsync(Limit);
        await AssertEndedWithoutErrorAsync(service);
    }

    [Fact]
    public async Task Logs_one_warning_when_the_deadline_passes()
    {
        var store = new GatedUrlStore();
        var logger = new FakeLogger<WarmUpService>();
        var (clock, service) = Create(Settings(deadlineSeconds: 15), store, logger);

        await service.StartAsync(CancellationToken.None);
        await store.WaitForArrivalsAsync(3);
        clock.Advance(TimeSpan.FromSeconds(15));
        await service.Ready.WaitAsync(Limit);

        var record = Assert.Single(logger.Collector.GetSnapshot());
        Assert.Equal(LogLevel.Warning, record.Level);
        Assert.Contains("deadline", record.Message);
    }

    [Fact]
    public async Task Stopping_during_warm_up_ends_without_an_error()
    {
        var store = new GatedUrlStore();
        var logger = new FakeLogger<WarmUpService>();
        var (_, service) = Create(Settings(), store, logger);

        await service.StartAsync(CancellationToken.None);
        await store.WaitForArrivalsAsync(3);
        await StopAsync(service);

        await AssertEndedWithoutErrorAsync(service);
        Assert.Empty(logger.Collector.GetSnapshot());
    }

    // lookups that failed are waiting for their retry; stopping must end that wait too
    [Fact]
    public async Task Stopping_during_a_retry_pause_ends_without_an_error()
    {
        var store = new FlakyUrlStore(failures: int.MaxValue);
        var logger = new FakeLogger<WarmUpService>();
        var (_, service) = Create(Settings(), store, logger);

        await service.StartAsync(CancellationToken.None);
        await store.WaitForCallsAsync(3);
        await Task.Delay(Pause);
        await StopAsync(service);

        await AssertEndedWithoutErrorAsync(service);
        Assert.Empty(logger.Collector.GetSnapshot());
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_service is null)
        {
            return;
        }

        await StopAsync(_service);
        Assert.True(
            _service.ExecuteTask?.IsCompleted ?? true,
            "ExecuteAsync still running 5 s after stop"
        );
    }

    // StopAsync never throws: it returns quietly when the limit passes or ExecuteAsync failed
    private static async Task StopAsync(WarmUpService service)
    {
        using var cts = new CancellationTokenSource(Limit);
        await service.StopAsync(cts.Token);
    }

    // An exception that leaves ExecuteAsync stops the whole app, so it has to end normally.
    // Awaiting the task rethrows a failure or a cancellation, which fails the test.
    private static async Task AssertEndedWithoutErrorAsync(WarmUpService service)
    {
        var run = service.ExecuteTask;
        Assert.NotNull(run);
        await run.WaitAsync(Limit);
    }

    private static WarmUpOptions Settings(
        int calls = 10,
        int parallelism = 3,
        int deadlineSeconds = 15
    ) =>
        new()
        {
            Calls = calls,
            Parallelism = parallelism,
            DeadlineSeconds = deadlineSeconds,
        };

    // the clock comes back so deadline tests can advance it; without a store, lookups go to an
    // empty in-memory store
    private (FakeTimeProvider Clock, WarmUpService Service) Create(
        WarmUpOptions warmUpOptions,
        IUrlStore? store = null,
        ILogger<WarmUpService>? logger = null
    )
    {
        var clock = new FakeTimeProvider(Now);
        store ??= new InMemoryUrlStore(clock);

        var service = new WarmUpService(
            store,
            clock,
            Options.Create(warmUpOptions),
            logger ?? NullLogger<WarmUpService>.Instance
        );
        _service = service;

        return (clock, service);
    }
}
