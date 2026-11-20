using StackExchange.Redis;
using Testcontainers.Redis;

namespace Shortener.Tests;

// One Redis container for every class in [Collection("redis")]. Tests don't flush it: each store
// gets its own key prefix (UniquePrefix), so tests never see each other's keys.
public sealed class RedisFixture : IAsyncLifetime
{
    private readonly RedisContainer _container = new RedisBuilder("redis:7").Build();
    private ConnectionMultiplexer? _redis;

    // CLIENT PAUSE is an admin command, refused unless the connection allows admin commands
    private ConnectionMultiplexer? _admin;

    public IConnectionMultiplexer Redis =>
        _redis ?? throw new InvalidOperationException("The fixture has not been initialized.");

    public string ConnectionString => _container.GetConnectionString();

    public static string UniquePrefix() => $"test:{Guid.NewGuid():N}:";

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        _redis = await ConnectionMultiplexer.ConnectAsync(ConnectionString);
        _admin = await ConnectionMultiplexer.ConnectAsync(ConnectionString + ",allowAdmin=true");
    }

    // A stalled Redis: the connection stays open, but Redis answers no client (except the one that
    // paused it) until `duration` ends or the returned handle is disposed, which unpauses it, so
    // the shared container doesn't hold up the next test. The app's client reports a stalled read
    // as RedisTimeoutException after up to about 1 s (it checks timeouts on a ~1 s heartbeat, not
    // at TimeoutMilliseconds), so pause for longer than that.
    public async Task<IAsyncDisposable> PauseAsync(TimeSpan duration)
    {
        var admin =
            _admin ?? throw new InvalidOperationException("The fixture has not been initialized.");
        await admin
            .GetDatabase()
            .ExecuteAsync("CLIENT", "PAUSE", (long)duration.TotalMilliseconds, "ALL");
        return new Unpause(admin);
    }

    private sealed class Unpause(ConnectionMultiplexer admin) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() =>
            await admin.GetDatabase().ExecuteAsync("CLIENT", "UNPAUSE");
    }

    public async Task DisposeAsync()
    {
        if (_redis is not null)
        {
            await _redis.DisposeAsync();
        }

        if (_admin is not null)
        {
            await _admin.DisposeAsync();
        }

        await _container.DisposeAsync();
    }
}

[CollectionDefinition("redis")]
public sealed class RedisCollection : ICollectionFixture<RedisFixture>;
