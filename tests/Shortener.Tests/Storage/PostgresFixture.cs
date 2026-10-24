using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
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

    public string ConnectionString => _container.GetConnectionString();

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

    // A new database in the same container with no tables, so a test can prove the app creates its own schema.
    public async Task<TemporaryDatabase> CreateEmptyDatabaseAsync()
    {
        var name = $"reg_{Guid.NewGuid():N}";
        await ExecuteAsync($"CREATE DATABASE \"{name}\"");

        var connectionString = new NpgsqlConnectionStringBuilder(ConnectionString)
        {
            Database = name,
        }.ConnectionString;

        return new TemporaryDatabase(
            connectionString,
            () => ExecuteAsync($"DROP DATABASE \"{name}\" WITH (FORCE)")
        );
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
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

public sealed class TemporaryDatabase(string connectionString, Func<Task> drop) : IAsyncDisposable
{
    public string ConnectionString { get; } = connectionString;

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await drop();
    }
}
