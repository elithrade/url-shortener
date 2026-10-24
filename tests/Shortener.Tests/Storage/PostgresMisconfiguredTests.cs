using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Shortener.Tests;

// A server that is up but refuses the request is a bug or a misconfiguration, not an outage,
// so the store must let the error escape untranslated (the endpoint then returns 500).
[Collection("postgres")]
public class PostgresMisconfiguredTests(PostgresFixture fixture)
{
    private PostgresUrlStore NewStoreForMissingDatabase()
    {
        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            Database = $"missing_{Guid.NewGuid():N}",
        }.ConnectionString;

        var provider = new ServiceCollection()
            .AddDbContextFactory<ShortenerDbContext>(o => o.UseNpgsql(connectionString))
            .BuildServiceProvider();

        return new PostgresUrlStore(
            provider.GetRequiredService<IDbContextFactory<ShortenerDbContext>>(),
            TimeProvider.System
        );
    }

    private static async Task AssertMissingDatabaseIsNotTranslated(Func<Task> operation)
    {
        var ex = await Record.ExceptionAsync(operation);

        Assert.NotNull(ex);
        Assert.IsNotType<StorageUnavailableException>(ex);

        // the setup must have produced the error we mean: 3D000, database does not exist
        var cause = ex;
        while (cause is not null and not PostgresException)
        {
            cause = cause.InnerException;
        }

        Assert.Equal("3D000", Assert.IsType<PostgresException>(cause).SqlState);
    }

    [Fact]
    public async Task TryAddAsync_does_not_translate_a_missing_database()
    {
        var store = NewStoreForMissingDatabase();

        await AssertMissingDatabaseIsNotTranslated(async () =>
            await store.TryAddAsync(
                "abc1234",
                new LinkEntry("https://example.com/", null),
                CancellationToken.None
            )
        );
    }

    [Fact]
    public async Task GetAsync_does_not_translate_a_missing_database()
    {
        var store = NewStoreForMissingDatabase();

        await AssertMissingDatabaseIsNotTranslated(async () =>
            await store.GetAsync("abc1234", CancellationToken.None)
        );
    }

    [Fact]
    public async Task SweepAsync_does_not_translate_a_missing_database()
    {
        var store = NewStoreForMissingDatabase();

        await AssertMissingDatabaseIsNotTranslated(async () =>
            await store.SweepAsync(CancellationToken.None)
        );
    }
}
