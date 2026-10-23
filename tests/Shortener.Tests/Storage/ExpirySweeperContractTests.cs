using Microsoft.Extensions.Time.Testing;

namespace Shortener.Tests;

public abstract class ExpirySweeperContractTests
{
    protected abstract (IUrlStore Store, IExpirySweeper Sweeper) Create(TimeProvider clock);

    [Fact]
    public async Task Sweep_removes_only_expired_entries()
    {
        DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var clock = new FakeTimeProvider(now);
        var (store, sweeper) = Create(clock);

        var expired = await AddManyAsync(store, "expired", 3, now.AddHours(1));
        var expiredNow = await AddManyAsync(store, "just-expired", 9, now.AddHours(2));
        var live = await AddManyAsync(store, "live", 9, now.AddDays(1));
        var never = await AddManyAsync(store, "never", 5, null);

        // advance time by 2 hours
        clock.Advance(TimeSpan.FromHours(2));

        int removed = await sweeper.SweepAsync(CancellationToken.None);

        // 3 expired and 9 just expired
        Assert.Equal(12, removed);

        // the first sweep must really remove them: GetAsync alone would hide it,
        // because it already treats an expired entry as missing
        Assert.Equal(0, await sweeper.SweepAsync(CancellationToken.None));

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
        var (store, sweeper) = Create(clock);

        Assert.True(
            await store.TryAddAsync(
                "abc",
                new LinkEntry("https://example.com/abc", now.AddHours(1)),
                CancellationToken.None
            )
        );

        clock.Advance(TimeSpan.FromHours(2));

        await sweeper.SweepAsync(CancellationToken.None);

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
        Assert.Equal(0, await sweeper.SweepAsync(CancellationToken.None));

        Assert.Equal(
            "https://newexample.com/abc",
            await store.GetAsync("abc", CancellationToken.None)
        );
    }

    private static async Task<IReadOnlyCollection<(string Code, string Url)>> AddManyAsync(
        IUrlStore store,
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
