using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Shortener;

// A cache-aside decorator: reads check Redis first and fill it on a miss; writes go only to the
// inner store. Redis is never required: a failed or unusable cache answer counts as a miss.
public sealed partial class CachedUrlStore(
    IUrlStore inner,
    IConnectionMultiplexer redis,
    TimeProvider clock,
    IOptions<CacheOptions> options,
    ILogger<CachedUrlStore> logger,
    IMeterFactory meterFactory
) : IUrlStore
{
    public const string MeterName = "Shortener.Cache";
    public const string ReadsCounterName = "shortener.cache.reads";

    private readonly IDatabase _redis = redis.GetDatabase();

    // one counter, split by the "result" tag (hit, miss or error); the hit rate is
    // hit / (hit + miss)
    private readonly Counter<long> _reads = meterFactory
        .Create(MeterName)
        .CreateCounter<long>(ReadsCounterName, description: "Cache reads by result");

    private async Task<(string Result, LinkEntry? Entry)> ReadCacheAsync(string code)
    {
        RedisValue cached;
        try
        {
            cached = await _redis.StringGetAsync(Key(code));
        }
        catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
        {
            CachedUrlStoreLog.RedisUnreachableWarning(logger, ex, "read");
            return ("error", null);
        }

        if (!cached.HasValue)
        {
            return ("miss", null);
        }

        // deserialize: null entry or JsonException -> Error, otherwise -> Hit with the entry
        try
        {
            var cachedEntry = JsonSerializer.Deserialize<LinkEntry>(
                cached.ToString(),
                JsonSerializerOptions.Strict
            );
            if (cachedEntry is not null)
            {
                return ("hit", cachedEntry);
            }

            // the cached text was "null"
            return ("error", null);
        }
        catch (JsonException ex)
        {
            CachedUrlStoreLog.DeserializeWarning(logger, ex);
            return ("error", null);
        }
    }

    public async ValueTask<LinkEntry?> GetAsync(string code, CancellationToken ct)
    {
        var (result, cachedEntry) = await ReadCacheAsync(code);
        Count(result);

        var now = clock.GetUtcNow();

        if (result == "hit" && cachedEntry is not null)
        {
            if (now >= cachedEntry.ExpiresAt)
            {
                // expired
                return null;
            }

            return cachedEntry;
        }

        LinkEntry? entry = await inner.GetAsync(code, ct);
        if (entry is not null)
        {
            var expireAt = entry.ExpiresAt;
            var ttl = TimeSpan.FromSeconds(options.Value.TtlSeconds);
            if (expireAt is not null)
            {
                var remaining = expireAt - now;
                ttl = Min(ttl, remaining.Value);
            }

            try
            {
                await _redis.StringSetAsync(Key(code), JsonSerializer.Serialize(entry), ttl);
            }
            catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
            {
                CachedUrlStoreLog.RedisUnreachableWarning(logger, ex, "write");
            }
        }

        return entry;
    }

    public ValueTask<bool> TryAddAsync(string code, LinkEntry linkEntry, CancellationToken ct) =>
        inner.TryAddAsync(code, linkEntry, ct);

    private void Count(string result) =>
        _reads.Add(1, new KeyValuePair<string, object?>("result", result));

    private static TimeSpan Min(TimeSpan one, TimeSpan two)
    {
        return one > two ? two : one;
    }

    private string Key(string code)
    {
        return $"{options.Value.KeyPrefix}{code}";
    }
}

internal static partial class CachedUrlStoreLog
{
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Error occurred when deserializing cached value"
    )]
    public static partial void DeserializeWarning(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cache {Operation} failed, using the store")]
    public static partial void RedisUnreachableWarning(
        ILogger logger,
        Exception exception,
        string operation
    );
}
