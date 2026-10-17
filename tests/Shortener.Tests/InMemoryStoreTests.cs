using Microsoft.Extensions.Time.Testing;

namespace Shortener.Tests;

public class InMemoryStoreTests
{
    private readonly TimeProvider _fakeTimeProvider = new FakeTimeProvider();

    [Fact]
    public async Task Store_guarantees_uniqueness_under_contention()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new InMemoryUrlStore(_fakeTimeProvider);
        var baseUrl = "https://example.com/";

        // simulate starting all 1000 tasks at once trying to add to the store
        var tasks = Enumerable
            .Range(0, 1000)
            .Select(i =>
                Task.Run(async () =>
                {
                    await gate.Task;
                    var linkEntry = new LinkEntry(baseUrl + i, null);
                    return await store.TryAddAsync("same", linkEntry, CancellationToken.None);
                })
            )
            .ToArray();

        // release all
        gate.SetResult();
        bool[] results = await Task.WhenAll(tasks);

        Assert.Equal(1000, results.Length);
        Assert.Equal(1, results.Count(x => x));

        var winner = results.IndexOf(true);
        Assert.Equal(baseUrl + winner, await store.GetAsync("same", CancellationToken.None));
    }

    [Fact]
    public async Task Distinct_keys_are_all_stored()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new InMemoryUrlStore(_fakeTimeProvider);
        var baseUrl = "https://example.com/";

        // simulate starting all 1000 tasks at once trying to add to the store
        var tasks = Enumerable
            .Range(0, 1000)
            .Select(i =>
                Task.Run(async () =>
                {
                    await gate.Task;

                    bool allAdded = true;
                    // each task adds unique keys
                    for (int j = 0; j < 10; j++)
                    {
                        var key = $"k{i}-{j}";
                        var linkEntry = new LinkEntry(baseUrl + key, null);

                        allAdded &= await store.TryAddAsync(key, linkEntry, CancellationToken.None);
                    }

                    return allAdded;
                })
            )
            .ToArray();

        // release and run all
        gate.SetResult();
        bool[] results = await Task.WhenAll(tasks);

        Assert.All(results, Assert.True);

        for (var i = 0; i < 1000; i++)
        {
            for (var j = 0; j < 10; j++)
            {
                var key = $"k{i}-{j}";
                Assert.Equal(baseUrl + key, await store.GetAsync(key, CancellationToken.None));
            }
        }
    }

    [Theory]
    [InlineData(0, true)] // just created
    [InlineData(3_599_999, true)] // 1 ms before expiry
    [InlineData(3_600_000, false)] // exactly at ExpiresAt: dead (your >=)
    [InlineData(3_600_001, false)] // 1 ms after
    public async Task Link_is_alive_until_its_expiry_instant(
        int advanceMilliseconds,
        bool expectedAlive
    )
    {
        DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(now);
        var store = new InMemoryUrlStore(clock);

        // add "abc" with ExpiresAt = Start + 1 hour
        await store.TryAddAsync(
            "abc",
            new LinkEntry("https://example.com/", now.AddHours(1)),
            CancellationToken.None
        );

        clock.Advance(TimeSpan.FromMilliseconds(advanceMilliseconds));

        // GetAsync("abc") is not null when expectedAlive, null otherwise
        var url = await store.GetAsync("abc", CancellationToken.None);
        Assert.Equal(expectedAlive, url != null);
    }

    [Fact]
    public async Task ExpiresAt_null_is_still_readable_after_advanced_default_time_span()
    {
        DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(now);
        var store = new InMemoryUrlStore(clock);

        await store.TryAddAsync(
            "abc",
            new LinkEntry("https://example.com/", null),
            CancellationToken.None
        );

        clock.Advance(TimeSpan.FromDays(365));

        var url = await store.GetAsync("abc", CancellationToken.None);
        Assert.NotNull(url);
    }

    [Fact]
    public async Task An_expired_code_cannot_be_re_added()
    {
        DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(now);
        var store = new InMemoryUrlStore(clock);

        await store.TryAddAsync(
            "abc",
            new LinkEntry("https://example.com/", now.AddHours(1)),
            CancellationToken.None
        );

        // code expired
        clock.Advance(TimeSpan.FromHours(2));

        // get should return null
        var url = await store.GetAsync("abc", CancellationToken.None);
        Assert.Null(url);

        Assert.False(
            await store.TryAddAsync(
                "abc",
                new LinkEntry("https://example.com/", null),
                CancellationToken.None
            )
        );
    }
}
