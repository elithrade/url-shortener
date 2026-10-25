using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Shortener.Tests;

[Collection("postgres")]
public class PostgresMigrationTests(PostgresFixture fixture)
{
    private readonly ILogger _logger = NullLogger<Program>.Instance;

    [Fact]
    public async Task Migrator_creates_valid_schema_on_empty_db()
    {
        await using var db = await fixture.CreateEmptyDatabaseAsync();
        var factory = db.Factory;

        Assert.False(await db.TableExistsAsync("links"));

        // migration succeeded
        var exitCode = await SchemaMigrator.ExecuteAsync(factory, _logger);
        Assert.Equal(0, exitCode);

        // check migrations
        await using var context = await factory.CreateDbContextAsync();
        var migrations = await context.Database.GetAppliedMigrationsAsync();
        bool initialCreate = migrations.Any(m =>
            m.EndsWith("InitialCreate", StringComparison.Ordinal)
        );
        Assert.True(initialCreate);

        Assert.True(await db.TableExistsAsync("links"));

        // create a new context
        await using var writeContext = await factory.CreateDbContextAsync();

        var entity = new LinkRecord { Code = "abc", Url = "https://example.com/path" };

        await writeContext.Links.AddAsync(entity);
        await writeContext.SaveChangesAsync();

        // create a new context for read
        await using var readContext = await factory.CreateDbContextAsync();
        var retrieved = await readContext.Links.Where(l => l.Code == "abc").FirstOrDefaultAsync();

        Assert.NotNull(retrieved);
        Assert.Equal("https://example.com/path", retrieved.Url);
        Assert.Equal("abc", retrieved.Code);
        Assert.Null(retrieved.ExpiresAt);
    }

    [Fact]
    public async Task Migrate_succeeds_when_run_twice()
    {
        await using var db = await fixture.CreateEmptyDatabaseAsync();
        var factory = db.Factory;

        Assert.Equal(0, await SchemaMigrator.ExecuteAsync(factory, _logger));

        await using var writeContext = await factory.CreateDbContextAsync();

        var entity = new LinkRecord { Code = "abc", Url = "https://example.com/path" };

        await writeContext.Links.AddAsync(entity);
        await writeContext.SaveChangesAsync();

        // migrate again
        Assert.Equal(0, await SchemaMigrator.ExecuteAsync(factory, _logger));

        // read still works using a different context
        await using var readContext = await factory.CreateDbContextAsync();
        var retrieved = await readContext.Links.Where(l => l.Code == "abc").FirstOrDefaultAsync();

        Assert.NotNull(retrieved);
        Assert.Equal("https://example.com/path", retrieved.Url);
        Assert.Equal("abc", retrieved.Code);
        Assert.Null(retrieved.ExpiresAt);
    }

    [Fact]
    public async Task Migration_fails_when_database_is_unreachable()
    {
        const string connectionString =
            "Host=127.0.0.1;Port=1;Username=x;Password=x;Database=x;Timeout=5";

        await using var provider = new ServiceCollection()
            .AddDbContextFactory<ShortenerDbContext>(o => o.UseNpgsql(connectionString))
            .BuildServiceProvider();

        var factory = provider.GetRequiredService<IDbContextFactory<ShortenerDbContext>>();
        var exitCode = await SchemaMigrator.ExecuteAsync(factory, _logger);

        Assert.Equal(1, exitCode);
    }
}
