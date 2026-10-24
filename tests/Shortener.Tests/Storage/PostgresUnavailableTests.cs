using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Shortener.Tests;

// Port 1 refuses connections, so no container is needed to get an unreachable database.
public class PostgresUnavailableTests
{
    private const string Unreachable =
        "Host=127.0.0.1;Port=1;Username=x;Password=x;Database=x;Timeout=5";

    private static PostgresUrlStore NewStore()
    {
        var provider = new ServiceCollection()
            .AddDbContextFactory<ShortenerDbContext>(o => o.UseNpgsql(Unreachable))
            .BuildServiceProvider();

        return new PostgresUrlStore(
            provider.GetRequiredService<IDbContextFactory<ShortenerDbContext>>(),
            TimeProvider.System
        );
    }

    [Fact]
    public async Task TryAddAsync_translates_an_unreachable_database()
    {
        var store = NewStore();
        var entry = new LinkEntry("https://example.com/", null);

        var ex = await Assert.ThrowsAsync<StorageUnavailableException>(async () =>
            await store.TryAddAsync("abc1234", entry, CancellationToken.None)
        );

        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public async Task GetAsync_translates_an_unreachable_database()
    {
        var store = NewStore();

        var ex = await Assert.ThrowsAsync<StorageUnavailableException>(async () =>
            await store.GetAsync("abc1234", CancellationToken.None)
        );

        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public async Task SweepAsync_translates_an_unreachable_database()
    {
        var store = NewStore();

        var ex = await Assert.ThrowsAsync<StorageUnavailableException>(async () =>
            await store.SweepAsync(CancellationToken.None)
        );

        Assert.NotNull(ex.InnerException);
    }
}
