using Npgsql;

namespace Shortener.Tests;

[Collection("postgres")]
public class PostgresClickStoreContractTests(PostgresFixture fixture)
    : ClickStoreContractTests,
        IAsyncLifetime
{
    // The concurrency test leaves up to 100 idle connections in the pool, and Postgres allows 100 in all, so
    // a later test opening its own pool (the health check) was refused. Close them after every test.
    public Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        return Task.CompletedTask;
    }

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
    }

    protected override IClickStore CreateStore()
    {
        return new PostgresClickStore(fixture.Factory);
    }

    // click_stats references links, so a code needs a real link before its clicks are counted
    protected override async Task AddLinkAsync(string code)
    {
        var links = new PostgresUrlStore(fixture.Factory, TimeProvider.System);
        await links.TryAddAsync(
            code,
            new LinkEntry("https://example.com/", null),
            CancellationToken.None
        );
    }

    [Fact]
    public async Task Clicks_on_codes_without_link_are_skipped()
    {
        await AddLinkAsync("abc");

        var store = CreateStore();
        var day = new DateOnly(2026, 1, 1);

        await store.AddAsync(
            [new DailyClicks("abc", day, 10), new DailyClicks("def", day, 20)],
            CancellationToken.None
        );

        var count = await store.GetAsync("abc", day, day, CancellationToken.None);
        Assert.Equal(10, count);

        count = await store.GetAsync("def", day, day, CancellationToken.None);
        Assert.Equal(0, count);
    }
}
