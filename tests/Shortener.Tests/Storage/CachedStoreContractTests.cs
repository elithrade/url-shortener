using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Shortener.Tests;

// The cache must not change what the store promises: the whole contract runs against a
// CachedUrlStore around an in-memory store, with a real Redis.
[Collection("redis")]
public class CachedStoreContractTests(RedisFixture redis) : UrlStoreContractTests
{
    protected override IUrlStore CreateStore(TimeProvider clock) =>
        new CachedUrlStore(
            new InMemoryUrlStore(clock),
            redis.Redis,
            clock,
            Options.Create(
                new CacheOptions
                {
                    TtlSeconds = 3600,
                    TimeoutMilliseconds = 1000,
                    KeyPrefix = RedisFixture.UniquePrefix(),
                }
            ),
            NullLogger<CachedUrlStore>.Instance
        );
}
