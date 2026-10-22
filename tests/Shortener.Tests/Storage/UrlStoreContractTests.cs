using Microsoft.Extensions.Time.Testing;

namespace Shortener.Tests;

public abstract class UrlStoreContractTests
{
    protected abstract IUrlStore CreateStore(TimeProvider clock);

    [Theory]
    [InlineData(-1)]
    [InlineData(-3600 * 1000 * 24)] // a day
    [InlineData(0)]
    public async Task Throws_exception_when_expiresAt_is_in_the_past(int milliseconds)
    {
        var (store, _, now) = Create();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
        {
            await store.TryAddAsync(
                "abc",
                new LinkEntry(
                    "https://example.com/",
                    now + TimeSpan.FromMilliseconds(milliseconds)
                ),
                CancellationToken.None
            );
        });
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3600 * 1000 * 24)]
    public async Task ExpiresAt_in_the_future_is_accepted(int milliseconds)
    {
        var (store, _, now) = Create();

        var added = await store.TryAddAsync(
            "abc",
            new LinkEntry("https://example.com/", now + TimeSpan.FromMilliseconds(milliseconds)),
            CancellationToken.None
        );

        Assert.True(added);
    }

    [Fact]
    public async Task Null_expiresAt_is_accepted()
    {
        var (store, _, _) = Create();

        var added = await store.TryAddAsync(
            "abc",
            new LinkEntry("https://example.com/", null),
            CancellationToken.None
        );

        Assert.True(added);
    }

    [Fact]
    public async Task Existing_code_with_past_expiry_throws_instead_of_returning_false()
    {
        var (store, _, now) = Create();

        var added = await store.TryAddAsync(
            "abc",
            new LinkEntry("https://example.com/", now + TimeSpan.FromMilliseconds(1)),
            CancellationToken.None
        );

        Assert.True(added);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
        {
            await store.TryAddAsync(
                "abc",
                new LinkEntry("https://example.com/", now + TimeSpan.FromMilliseconds(-1)),
                CancellationToken.None
            );
        });
    }

    [Fact]
    public async Task Code_can_be_added_after_rejected_past_expiry()
    {
        var (store, _, now) = Create();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
        {
            await store.TryAddAsync(
                "abc",
                new LinkEntry("https://example.com/", now + TimeSpan.FromSeconds(-1)),
                CancellationToken.None
            );
        });

        var added = await store.TryAddAsync(
            "abc",
            new LinkEntry("https://example.com/", now + TimeSpan.FromSeconds(1)),
            CancellationToken.None
        );

        Assert.True(added);
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
        var (store, clock, now) = Create();

        // add "abc" with ExpiresAt = now + 1 hour
        Assert.True(
            await store.TryAddAsync(
                "abc",
                new LinkEntry("https://example.com/", now.AddHours(1)),
                CancellationToken.None
            )
        );

        clock.Advance(TimeSpan.FromMilliseconds(advanceMilliseconds));

        // GetAsync("abc") is not null when expectedAlive, null otherwise
        var url = await store.GetAsync("abc", CancellationToken.None);
        Assert.Equal(expectedAlive, url != null);
    }

    [Fact]
    public async Task Expiry_with_a_non_utc_offset_is_accepted()
    {
        var (store, _, now) = Create();
        var expiresAt = now.AddHours(1).ToOffset(TimeSpan.FromHours(8));

        // same instant as now + 1 hour, written with a +08:00 offset
        Assert.Equal(now.AddHours(1), expiresAt);
        Assert.Equal(TimeSpan.FromHours(8), expiresAt.Offset);

        Assert.True(
            await store.TryAddAsync(
                "abc",
                new LinkEntry("https://example.com/", expiresAt),
                CancellationToken.None
            )
        );
    }

    [Theory]
    [InlineData(3_599_999, true)] // 1 ms before the instant
    [InlineData(3_600_000, false)] // exactly at the instant
    public async Task Link_with_a_non_utc_expiry_is_alive_until_that_instant(
        int advanceMilliseconds,
        bool expectedAlive
    )
    {
        var (store, clock, now) = Create();
        var expiresAt = now.AddHours(1).ToOffset(TimeSpan.FromHours(8));
        Assert.Equal(TimeSpan.FromHours(8), expiresAt.Offset);

        Assert.True(
            await store.TryAddAsync(
                "abc",
                new LinkEntry("https://example.com/", expiresAt),
                CancellationToken.None
            )
        );

        clock.Advance(TimeSpan.FromMilliseconds(advanceMilliseconds));

        var url = await store.GetAsync("abc", CancellationToken.None);
        Assert.Equal(expectedAlive, url != null);
    }

    [Fact]
    public async Task Never_expiring_link_is_still_readable_after_a_year()
    {
        var (store, clock, _) = Create();

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
    public async Task An_expired_code_stays_taken_until_sweep()
    {
        var (store, clock, now) = Create();

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

    [Fact]
    public async Task Only_one_of_many_concurrent_adds_of_the_same_code_wins()
    {
        var (store, _, _) = Create();

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var baseUrl = "https://example.com/";

        var tasks = Enumerable
            .Range(0, 50)
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

        Assert.Equal(50, results.Length);
        Assert.Equal(1, results.Count(x => x));

        var winner = results.IndexOf(true);
        Assert.Equal(baseUrl + winner, await store.GetAsync("same", CancellationToken.None));
    }

    [Fact]
    public async Task Case_sensitive_code_treated_as_different_entries()
    {
        var (store, _, now) = Create();

        Assert.True(
            await store.TryAddAsync(
                "abc",
                new LinkEntry("https://example.com/1", now.AddHours(1)),
                CancellationToken.None
            )
        );

        Assert.True(
            await store.TryAddAsync(
                "aBc",
                new LinkEntry("https://example.com/2", now.AddHours(1)),
                CancellationToken.None
            )
        );

        var url = await store.GetAsync("abc", CancellationToken.None);
        Assert.Equal("https://example.com/1", url);

        url = await store.GetAsync("aBc", CancellationToken.None);
        Assert.Equal("https://example.com/2", url);
    }

    private (IUrlStore Store, FakeTimeProvider Clock, DateTimeOffset Now) Create()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var store = CreateStore(clock);
        var now = clock.GetUtcNow();

        return (store, clock, now);
    }
}
