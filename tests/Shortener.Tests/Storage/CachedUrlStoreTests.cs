using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using StackExchange.Redis;

namespace Shortener.Tests;

[Collection("redis")]
public class CachedUrlStoreTests(RedisFixture redis)
{
    [Fact]
    public async Task A_miss_on_a_known_code_fills_the_cache()
    {
        var (cachedStore, innerStore, _, prefix) = Create();

        var expiresAt = Now.AddHours(1);
        await innerStore.TryAddAsync(
            "abc",
            new LinkEntry("https://example.com/a", expiresAt),
            CancellationToken.None
        );

        var url = (await cachedStore.GetAsync("abc", CancellationToken.None))?.Url;
        Assert.Equal("https://example.com/a", url);

        var cached = await redis.Redis.GetDatabase().StringGetAsync(prefix + "abc");
        Assert.True(cached.HasValue);

        var linkEntry = JsonSerializer.Deserialize<LinkEntry>(cached.ToString());

        Assert.Equal(new LinkEntry("https://example.com/a", expiresAt), linkEntry);
    }

    // A hit must check ExpiresAt against the app's clock, not trust Redis's TTL, and at the
    // same boundary as the inner store (expired when now >= ExpiresAt).
    [Theory]
    [InlineData(10 * 60 * 1000 - 1, true)] // 1 ms before ExpiresAt: a valid hit
    [InlineData(10 * 60 * 1000, false)] // exactly at ExpiresAt: expired
    [InlineData(20 * 60 * 1000, false)] // long after
    public async Task A_hit_checks_expiry_against_the_app_clock(
        int advanceMilliseconds,
        bool expectedAlive
    )
    {
        var (cachedStore, innerStore, clock, prefix) = Create();
        var entry = new LinkEntry("https://example.com/a", Now.AddMinutes(10));
        await innerStore.TryAddAsync("abc", entry, CancellationToken.None);

        // a miss: fills the cache
        Assert.Equal(entry, await cachedStore.GetAsync("abc", CancellationToken.None));

        clock.Advance(TimeSpan.FromMilliseconds(advanceMilliseconds));

        // redis has the real clock so the record still there
        var cached = await redis.Redis.GetDatabase().StringGetAsync(prefix + "abc");
        Assert.True(cached.HasValue);

        // so this read is a hit, and only the app's clock can filter it out
        var afterAdvance = await cachedStore.GetAsync("abc", CancellationToken.None);
        Assert.Equal(expectedAlive ? entry : null, afterAdvance);
    }

    // Bug 1: Redis down counts as a miss, and it must not slow the redirect down.
    [Fact]
    public async Task Redis_down_falls_back_to_the_inner_store()
    {
        // nothing listens on port 1; the connection uses the same settings as production
        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(
            RedisConnection.Configure("localhost:1", new CacheOptions { TimeoutMilliseconds = 200 })
        );
        var (cachedStore, innerStore, _, _) = Create(multiplexer);

        var entry = new LinkEntry("https://example.com/a", Now.AddMinutes(10));
        await innerStore.TryAddAsync("abc", entry, CancellationToken.None);

        var stopwatch = Stopwatch.StartNew();
        var result = await cachedStore.GetAsync("abc", CancellationToken.None);
        stopwatch.Stop();

        Assert.Equal(entry, result);

        // guards against the client's 5 s default queued command
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"took {stopwatch.Elapsed}");
    }

    // Bug 3: a cached value the app can't use is a miss, not "not found".
    [Theory]
    [InlineData("null")] // valid JSON that deserializes to null
    [InlineData("not json")] // throws JsonException
    [InlineData("{}")] // strict options treat it as invalid json
    public async Task An_unusable_cached_value_falls_back_to_the_inner_store(string cachedValue)
    {
        var (cachedStore, innerStore, _, prefix) = Create();
        var entry = new LinkEntry("https://example.com/a", Now.AddMinutes(10));
        await innerStore.TryAddAsync("abc", entry, CancellationToken.None);

        await redis.Redis.GetDatabase().StringSetAsync(prefix + "abc", cachedValue);

        var result = await cachedStore.GetAsync("abc", CancellationToken.None);
        Assert.Equal(entry, result);
    }

    // The cache's purpose: a hit saves the read of the inner store.
    [Fact]
    public async Task A_hit_does_not_read_the_inner_store()
    {
        var (cachedStore, innerStore, _, _) = Create();
        var entry = new LinkEntry("https://example.com/a", Now.AddMinutes(10));
        await innerStore.TryAddAsync("abc", entry, CancellationToken.None);

        var first = await cachedStore.GetAsync("abc", CancellationToken.None);
        Assert.Equal(entry, first);

        var second = await cachedStore.GetAsync("abc", CancellationToken.None);
        Assert.Equal(entry, second);

        var third = await cachedStore.GetAsync("abc", CancellationToken.None);
        Assert.Equal(entry, third);

        Assert.Equal(1, innerStore.Gets);
    }

    // The key lives no longer than the link, and no longer than Cache:TtlSeconds (3600 here).
    [Theory]
    [InlineData(10, 9, 10)] // expires in 10 min: the link's remaining lifetime wins
    [InlineData(120, 59, 60)] // expires in 2 h: the cache TTL wins
    [InlineData(null, 59, 60)] // never expires: the cache TTL
    public async Task The_key_ttl_is_the_smaller_of_cache_ttl_and_remaining_lifetime(
        int? expiresInMinutes,
        int minTtlMinutes,
        int maxTtlMinutes
    )
    {
        var (cachedStore, innerStore, _, prefix) = Create();
        var entry = new LinkEntry(
            "https://example.com/a",
            expiresInMinutes is null ? null : Now.AddMinutes(expiresInMinutes.Value)
        );
        await innerStore.TryAddAsync("abc", entry, CancellationToken.None);
        await cachedStore.GetAsync("abc", CancellationToken.None);

        var ttl = await redis.Redis.GetDatabase().KeyTimeToLiveAsync(prefix + "abc");

        Assert.NotNull(ttl);
        Assert.InRange(
            ttl.Value,
            TimeSpan.FromMinutes(minTtlMinutes),
            TimeSpan.FromMinutes(maxTtlMinutes)
        );
    }

    // "Not found" is not cached: a code created later must work on its first read.
    [Fact]
    public async Task A_miss_on_an_unknown_code_caches_nothing()
    {
        var (cachedStore, innerStore, _, prefix) = Create();
        var result = await cachedStore.GetAsync("abc", CancellationToken.None);
        Assert.Null(result);
        Assert.False(await redis.Redis.GetDatabase().KeyExistsAsync(prefix + "abc"));

        var entry = new LinkEntry("https://example.com/a", Now.AddMinutes(10));
        await innerStore.TryAddAsync("abc", entry, CancellationToken.None);

        // the entry comes back (a cached "not found" would still return null)
        result = await cachedStore.GetAsync("abc", CancellationToken.None);
        Assert.Equal(entry, result);
    }

    // Each GetAsync counts exactly one read: hit, miss or error.
    [Fact]
    public async Task Reads_are_counted_by_result()
    {
        var (cachedStore, innerStore, _, prefix) = Create();
        var entry = new LinkEntry("https://example.com/a", Now.AddMinutes(10));
        await innerStore.TryAddAsync("abc", entry, CancellationToken.None);

        await cachedStore.GetAsync("abc", CancellationToken.None); // miss
        await cachedStore.GetAsync("abc", CancellationToken.None); // hit

        Assert.Equal(1, Reads("miss"));
        Assert.Equal(1, Reads("hit"));

        await cachedStore.GetAsync("nope", CancellationToken.None); // miss

        Assert.Equal(2, Reads("miss"));
        Assert.Equal(0, Reads("error"));

        await redis.Redis.GetDatabase().StringSetAsync(prefix + "abc", "{}");
        await cachedStore.GetAsync("abc", CancellationToken.None); // error

        Assert.Equal(1, Reads("error"));
        Assert.Equal(2, Reads("miss"));
        Assert.Equal(1, Reads("hit"));

        await redis.Redis.GetDatabase().StringSetAsync(prefix + "abc", "null");
        await cachedStore.GetAsync("abc", CancellationToken.None); // error

        Assert.Equal(2, Reads("error"));
        Assert.Equal(2, Reads("miss"));
        Assert.Equal(1, Reads("hit"));
    }

    [Fact]
    public async Task A_failed_redis_read_counts_as_an_error()
    {
        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(
            RedisConnection.Configure("localhost:1", new CacheOptions { TimeoutMilliseconds = 200 })
        );
        var (errorCachedStore, errorInnerStore, _, _) = Create(multiplexer);

        var entry = new LinkEntry("https://example.com/a", Now.AddMinutes(10));
        await errorInnerStore.TryAddAsync("abc", entry, CancellationToken.None);

        await errorCachedStore.GetAsync("abc", CancellationToken.None); // error

        Assert.Equal(1, Reads("error"));
        Assert.Equal(0, Reads("miss"));
        Assert.Equal(0, Reads("hit"));
    }

    private static readonly DateTimeOffset Now = new(2026, 1, 1, 22, 0, 0, TimeSpan.Zero);

    // set by Create(); xUnit makes a new instance of this class for each test
    private MetricCollector<long>? _reads;

    // how many reads the store created by Create() has counted with this result tag
    private long Reads(string result) =>
        (_reads ?? throw new InvalidOperationException("Call Create() first."))
            .GetMeasurementSnapshot()
            .Where(m => (string?)m.Tags["result"] == result)
            .Sum(m => m.Value);

    // a cached store around an in-memory store, with its own key prefix; pass a multiplexer to
    // replace the shared container's (for the Redis-down test). Inner counts its reads (Gets).
    private (
        CachedUrlStore Store,
        CountingUrlStore Inner,
        FakeTimeProvider Clock,
        string Prefix
    ) Create(IConnectionMultiplexer? multiplexer = null)
    {
        var clock = new FakeTimeProvider(Now);
        var inner = new CountingUrlStore(new InMemoryUrlStore(clock));
        var prefix = RedisFixture.UniquePrefix();
        var meters = TestMeters.CreateFactory();
        _reads = new MetricCollector<long>(
            meters,
            CachedUrlStore.MeterName,
            CachedUrlStore.ReadsCounterName
        );

        var store = new CachedUrlStore(
            inner,
            multiplexer ?? redis.Redis,
            clock,
            Options.Create(
                new CacheOptions
                {
                    TtlSeconds = 3600,
                    TimeoutMilliseconds = 1000,
                    KeyPrefix = prefix,
                }
            ),
            NullLogger<CachedUrlStore>.Instance,
            meters
        );

        return (store, inner, clock, prefix);
    }
}
