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
    ILogger<CachedUrlStore> logger
) : IUrlStore
{
    private readonly IDatabase _redis = redis.GetDatabase();

    public async ValueTask<LinkEntry?> GetAsync(string code, CancellationToken ct)
    {
        RedisValue cached = default;
        try
        {
            cached = await _redis.StringGetAsync(Key(code));
        }
        // RedisTimeoutException derives from TimeoutException, not RedisException
        catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
        {
            CachedUrlStoreLog.RedisUnreachableWarning(logger, ex, "read");
        }

        try
        {
            if (cached.HasValue)
            {
                var cachedEntry = JsonSerializer.Deserialize<LinkEntry>(
                    cached.ToString(),
                    JsonSerializerOptions.Strict
                );
                if (cachedEntry is not null)
                {
                    if (clock.GetUtcNow() >= cachedEntry.ExpiresAt)
                    {
                        // expired
                        return null;
                    }

                    return cachedEntry;
                }
            }
        }
        catch (JsonException ex)
        {
            CachedUrlStoreLog.DeserializeWarning(logger, ex);
        }

        var now = clock.GetUtcNow();
        LinkEntry? entry = await inner.GetAsync(code, ct);
        if (entry is not null)
        {
            var expireAt = entry.ExpiresAt;
            var ttl = TimeSpan.FromSeconds(options.Value.TtlSeconds);
            if (expireAt is not null)
            {
                var remaining = expireAt - now;
                if (remaining <= TimeSpan.Zero)
                {
                    return entry;
                }
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
