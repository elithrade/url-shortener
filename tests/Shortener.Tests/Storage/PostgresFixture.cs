using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace Shortener.Tests;

// One container per test class. Registered the same way as in production, so tests exercise the real factory setup.
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17").Build();
    private ServiceProvider? _provider;

    public IDbContextFactory<ShortenerDbContext> Factory =>
        _provider?.GetRequiredService<IDbContextFactory<ShortenerDbContext>>()
        ?? throw new InvalidOperationException("The fixture has not been initialized.");

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        _provider = new ServiceCollection()
            .AddDbContextFactory<ShortenerDbContext>(o =>
                o.UseNpgsql(_container.GetConnectionString())
            )
            .BuildServiceProvider();

        await using var db = await Factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
    }

    public async Task ResetAsync()
    {
        await using var db = await Factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE links");
    }

    public async Task DisposeAsync()
    {
        if (_provider is not null)
        {
            await _provider.DisposeAsync();
        }

        await _container.DisposeAsync();
    }
}
