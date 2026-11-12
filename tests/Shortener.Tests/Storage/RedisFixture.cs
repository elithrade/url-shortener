using StackExchange.Redis;
using Testcontainers.Redis;

namespace Shortener.Tests;

// One Redis container for every class in [Collection("redis")]. Tests don't flush it: each store
// gets its own key prefix (UniquePrefix), so tests never see each other's keys.
public sealed class RedisFixture : IAsyncLifetime
{
    private readonly RedisContainer _container = new RedisBuilder("redis:7").Build();
    private ConnectionMultiplexer? _redis;

    public IConnectionMultiplexer Redis =>
        _redis ?? throw new InvalidOperationException("The fixture has not been initialized.");

    public string ConnectionString => _container.GetConnectionString();

    public static string UniquePrefix() => $"test:{Guid.NewGuid():N}:";

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        _redis = await ConnectionMultiplexer.ConnectAsync(ConnectionString);
    }

    public async Task DisposeAsync()
    {
        if (_redis is not null)
        {
            await _redis.DisposeAsync();
        }

        await _container.DisposeAsync();
    }
}

[CollectionDefinition("redis")]
public sealed class RedisCollection : ICollectionFixture<RedisFixture>;
