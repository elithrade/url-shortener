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

    [Fact]
    public async Task Sweep_removes_only_expired_entries()
    {
        DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var clock = new FakeTimeProvider(now);
        var store = new InMemoryUrlStore(clock);

        var expired = await AddManyAsync(store, "expired", 3, now.AddHours(1));
        var expiredNow = await AddManyAsync(store, "just-expired", 9, now.AddHours(2));
        var live = await AddManyAsync(store, "live", 9, now.AddDays(1));
        var never = await AddManyAsync(store, "never", 5, null);

        // advance time by 2 hours
        clock.Advance(TimeSpan.FromHours(2));

        int removed = await store.SweepAsync(CancellationToken.None);

        // 3 expired and 9 just expired
        Assert.Equal(12, removed);

        // the first sweep must really remove them: GetAsync alone would hide it,
        // because it already treats an expired entry as missing
        Assert.Equal(0, await store.SweepAsync(CancellationToken.None));

        // getting expired should return null
        foreach (var (Code, _) in expired)
        {
            Assert.Null(await store.GetAsync(Code, CancellationToken.None));
        }
        foreach (var (Code, _) in expiredNow)
        {
            Assert.Null(await store.GetAsync(Code, CancellationToken.None));
        }

        // live and never should return url
        foreach (var (Code, Url) in live)
        {
            Assert.Equal(Url, await store.GetAsync(Code, CancellationToken.None));
        }
        foreach (var (Code, Url) in never)
        {
            Assert.Equal(Url, await store.GetAsync(Code, CancellationToken.None));
        }
    }

    [Fact]
    public async Task Swept_code_can_be_added_again()
    {
        DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var clock = new FakeTimeProvider(now);
        var store = new InMemoryUrlStore(clock);

        Assert.True(
            await store.TryAddAsync(
                "abc",
                new LinkEntry("https://example.com/abc", now.AddHours(1)),
                CancellationToken.None
            )
        );

        clock.Advance(TimeSpan.FromHours(2));

        await store.SweepAsync(CancellationToken.None);

        // the store by design allows the same code be added again once its expired and swept away
        Assert.True(
            await store.TryAddAsync(
                "abc",
                new LinkEntry("https://newexample.com/abc", clock.GetUtcNow().AddHours(1)),
                CancellationToken.None
            )
        );

        clock.Advance(TimeSpan.FromMinutes(30));
        // not expired and not swept away
        Assert.Equal(0, await store.SweepAsync(CancellationToken.None));

        Assert.Equal(
            "https://newexample.com/abc",
            await store.GetAsync("abc", CancellationToken.None)
        );
    }

    private static async Task<IReadOnlyCollection<(string Code, string Url)>> AddManyAsync(
        InMemoryUrlStore store,
        string prefix,
        int count,
        DateTimeOffset? expiresAt
    )
    {
        var values = new List<(string Code, string Url)>();

        for (int i = 0; i < count; i++)
        {
            var code = $"{prefix}-{i}";
            var url = $"https://example.com/{prefix}/{i}";

            // fail loudly if setup fails, so assertions over the returned list can't pass vacuously
            Assert.True(
                await store.TryAddAsync(code, new LinkEntry(url, expiresAt), CancellationToken.None)
            );
            values.Add((code, url));
        }

        return values.AsReadOnly();
    }
}
