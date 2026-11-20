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

    // Jittered TTLs: keys filled together must not all expire together. Each test turns jitter on
    // with Create(ttlJitterPercent: 10), so a key's TTL is 3600 s shortened by 0 to 10%.

    // Seed and read 50 codes that never expire, then read each key's TTL: every one is within
    // [3240 s, 3600 s]. Passes without jitter too (3600 is in range); the spread test is the red one.
    [Fact]
    public async Task A_jittered_ttl_stays_between_the_lower_bound_and_the_cache_ttl()
    {
        var (cachedStore, innerStore, _, prefix) = Create(ttlJitterPercent: 10);

        var ttls = await FillAndReadTtlsAsync(cachedStore, innerStore, prefix, 50);

        // 5 s slack: time passes between the SET and this read
        Assert.All(ttls, ttl =>
            Assert.InRange(ttl, TimeSpan.FromSeconds(3240 - 5), TimeSpan.FromSeconds(3600))
        );
    }

    // Same 50 keys: the largest TTL minus the smallest is over 60 s. Not "not all equal": Redis
    // reports milliseconds and time passes between SETs, so TTLs differ even without jitter.
    [Fact]
    public async Task Jittered_ttls_of_keys_filled_together_spread_out()
    {
        var (cachedStore, innerStore, _, prefix) = Create(ttlJitterPercent: 10);

        var ttls = await FillAndReadTtlsAsync(cachedStore, innerStore, prefix, 50);
        var spread = ttls.Max() - ttls.Min();

        Assert.True(spread > TimeSpan.FromSeconds(60), $"Spread was {spread}");
        Assert.InRange(spread, TimeSpan.FromSeconds(60), TimeSpan.MaxValue);
    }

    // Links that expire in 30 min, with jitter on: each TTL is at most 30 min, so jitter never
    // lets a cached entry outlive its link. (The fake clock doesn't move, so 30 min is exact.)
    // The lower bound catches jitter applied after the min, which cuts 0 to 6 min. One link
    // missed that 1 run in 6 (a cut under 1 min); with 20, all would need one, about (1/6)^20.
    [Fact]
    public async Task A_jittered_ttl_never_outlives_the_link()
    {
        var (cachedStore, innerStore, clock, prefix) = Create(ttlJitterPercent: 10);

        var ttls = await FillAndReadTtlsAsync(
            cachedStore,
            innerStore,
            prefix,
            20,
            expiresAt: clock.GetUtcNow() + TimeSpan.FromMinutes(30)
        );

        Assert.All(ttls, ttl =>
            Assert.InRange(ttl, TimeSpan.FromMinutes(29), TimeSpan.FromMinutes(30))
        );
    }

    // A link that expires between the store's read and the cache's TTL calculation gives a
    // remaining lifetime of zero or less. Redis can't take that TTL (the client throws before
    // sending), so the cache must skip the SET instead of failing the read.
    // Create(innerStore: new FixedEntryStore(new LinkEntry(url, Now.AddSeconds(-1)))): the store
    // hands the cache an entry that is already expired. Read it through the cache: no exception,
    // and no key in Redis (KeyExistsAsync is false).
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_link_that_expires_during_the_read_is_not_cached(int offsetSeconds)
    {
        var (cachedStore, _, _, prefix) = Create(
            innerStore: new FixedEntryStore(
                new LinkEntry("https://example.com", Now.AddSeconds(offsetSeconds))
            )
        );

        var entry = await cachedStore.GetAsync("abc", CancellationToken.None);

        Assert.NotNull(entry);
        Assert.False(await redis.Redis.GetDatabase().KeyExistsAsync(prefix + "abc"));
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

    // Single-flight: concurrent misses for one code share one inner lookup. Each test holds the
    // inner store with a GatedUrlStore: Create(innerStore: gated).

    // Fire with Concurrently.RunAsync, WaitForArrivalsAsync(1), pause ~200 ms, assert
    // MaxInFlight == 1 (and Inner.Gets == 1) before Release; then every caller gets the result.
    [Fact]
    public async Task Concurrent_misses_for_one_code_share_one_inner_lookup()
    {
        var gated = new GatedUrlStore();
        try
        {
            var (store, innerStore, _, _) = Create(innerStore: gated);

            var run = Concurrently.RunAsync(
                100, // 100 callers flooding the store
                async _ => await store.GetAsync("abc", CancellationToken.None)
            );

            await gated.WaitForArrivalsAsync(1);
            // give other 99 callers to reach the shared wait
            await Task.Delay(200);

            gated.Release();

            var results = await run.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.All(results, Assert.Null);
            Assert.Equal(1, gated.MaxInFlight);
            Assert.Equal(1, innerStore.Gets);
        }
        finally
        {
            gated.Release();
        }
    }

    // Hold the lookup, then gated.Fail(...): every waiter throws that exception. A read after
    // that reaches the inner store again (Inner.Gets == 2), so the failure was not kept.
    [Fact]
    public async Task A_failed_lookup_fails_every_waiter_and_the_next_read_retries()
    {
        var gated = new GatedUrlStore();
        try
        {
            var (store, innerStore, _, _) = Create(innerStore: gated);

            var run = Concurrently.RunAsync(100, async _ =>
            {
                try
                {
                    await store.GetAsync("abc", CancellationToken.None);
                    return null;
                }
                catch (Exception ex)
                {
                    return ex;
                }
            });

            await gated.WaitForArrivalsAsync(1);
            // give other 99 callers to reach the shared wait
            await Task.Delay(200);

            var exception = new StorageUnavailableException(
                "Store not ready",
                new TimeoutException()
            );
            gated.Fail(exception);

            var results = await run.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.All(results, r => Assert.Same(exception, r));

            Assert.Equal(1, gated.MaxInFlight);
            Assert.Equal(1, innerStore.Gets);

            await Assert.ThrowsAsync<StorageUnavailableException>(async () =>
                await store.GetAsync("abc", CancellationToken.None)
            );

            Assert.Equal(2, innerStore.Gets);
        }
        finally
        {
            gated.Release();
        }
    }

    // Two callers wait on a held lookup; cancel one's token. It throws OperationCanceledException,
    // gated.InFlight stays 1 (the shared lookup was not cancelled), and after Release the other
    // caller completes.
    [Fact]
    public async Task A_cancelled_caller_does_not_cancel_the_shared_lookup()
    {
        var gated = new GatedUrlStore();
        try
        {
            var (store, innerStore, _, _) = Create(innerStore: gated);

            using var sourceOne = new CancellationTokenSource();

            var taskOne = store.GetAsync("abc", sourceOne.Token).AsTask();
            await gated.WaitForArrivalsAsync(1);

            var taskB = store.GetAsync("abc", CancellationToken.None).AsTask();
            await Task.Delay(200);

            sourceOne.Cancel();
            // the timeout makes a caller that ignores its token fail here instead of hanging
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                taskOne.WaitAsync(TimeSpan.FromSeconds(5))
            );

            Assert.Equal(1, gated.InFlight);
            gated.Release();

            var result = await taskB.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(result);
            Assert.Equal(1, innerStore.Gets);
        }
        finally
        {
            gated.Release();
        }
    }

    // Hold a read of one code, then read another: both reach the gate (MaxInFlight == 2).
    [Fact]
    public async Task Misses_for_different_codes_do_not_wait_for_each_other()
    {
        var gated = new GatedUrlStore();
        try
        {
            var (store, innerStore, _, _) = Create(innerStore: gated);

            var taskOne = store.GetAsync("abc", CancellationToken.None).AsTask();
            var taskTwo = store.GetAsync("def", CancellationToken.None).AsTask();
            await gated.WaitForArrivalsAsync(2);

            gated.Release();

            var results = await Task.WhenAll(taskOne, taskTwo).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.All(results, Assert.Null);
            Assert.Equal(2, gated.MaxInFlight);
            Assert.Equal(2, innerStore.Gets);
        }
        finally
        {
            gated.Release();
        }
    }

    // After a round completes, a new read reaches the inner store again. GatedUrlStore returns
    // null, which never fills Redis, so the second read is a real miss.
    [Fact]
    public async Task A_finished_lookup_is_not_left_in_flight()
    {
        var gated = new GatedUrlStore();
        try
        {
            var (store, innerStore, _, _) = Create(innerStore: gated);

            var taskOne = store.GetAsync("abc", CancellationToken.None).AsTask();
            await gated.WaitForArrivalsAsync(1);

            gated.Release();

            var result = await taskOne.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(result);

            result = await store.GetAsync("abc", CancellationToken.None);

            Assert.Null(result);
            Assert.Equal(2, innerStore.Gets);
        }
        finally
        {
            gated.Release();
        }
    }

    // set by Create(); xUnit makes a new instance of this class for each test
    private MetricCollector<long>? _reads;

    // how many reads the store created by Create() has counted with this result tag
    private long Reads(string result) =>
        (_reads ?? throw new InvalidOperationException("Call Create() first."))
            .GetMeasurementSnapshot()
            .Where(m => (string?)m.Tags["result"] == result)
            .Sum(m => m.Value);

    // a cached store around an in-memory store, with its own key prefix; pass a multiplexer to
    // replace the shared container's (for the Redis-down test), or an innerStore to use in place of
    // the in-memory one (a GatedUrlStore for single-flight). Inner counts its reads (Gets).
    // Jitter is off unless a test turns it on, so the other TTL tests stay exact.
    private (
        CachedUrlStore Store,
        CountingUrlStore Inner,
        FakeTimeProvider Clock,
        string Prefix
    ) Create(
        IConnectionMultiplexer? multiplexer = null,
        IUrlStore? innerStore = null,
        int ttlJitterPercent = 0
    )
    {
        var clock = new FakeTimeProvider(Now);
        var inner = new CountingUrlStore(innerStore ?? new InMemoryUrlStore(clock));
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
                    TtlJitterPercent = ttlJitterPercent,
                    TimeoutMilliseconds = 1000,
                    KeyPrefix = prefix,
                }
            ),
            NullLogger<CachedUrlStore>.Instance,
            meters
        );

        return (store, inner, clock, prefix);
    }

    // Seeds `count` links (that never expire, unless given expiresAt), reads each through the
    // cache (a miss, so the cache fills the key with its TTL) and returns every key's TTL as
    // Redis reports it.
    private async Task<List<TimeSpan>> FillAndReadTtlsAsync(
        CachedUrlStore cachedStore,
        CountingUrlStore innerStore,
        string prefix,
        int count,
        DateTimeOffset? expiresAt = null
    )
    {
        for (var i = 0; i < count; i++)
        {
            await innerStore.TryAddAsync(
                "code" + i,
                new LinkEntry($"https://example.com/{i}", expiresAt),
                CancellationToken.None
            );
        }

        for (var i = 0; i < count; i++)
        {
            await cachedStore.GetAsync("code" + i, CancellationToken.None);
        }

        var ttls = new List<TimeSpan>();
        for (var i = 0; i < count; i++)
        {
            var ttl = await redis.Redis.GetDatabase().KeyTimeToLiveAsync(prefix + "code" + i);
            Assert.NotNull(ttl); // the key exists and has an expiry
            ttls.Add(ttl.Value);
        }

        return ttls;
    }
}
