using System.Collections.Concurrent;

namespace Shortener;

public sealed class TokenBucketLimiter(TokenBucketOptions options, TimeProvider clock)
    : IRateLimiter
{
    private readonly TokenBucketOptions _options =
        options ?? throw new ArgumentNullException(nameof(options));

    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly ConcurrentDictionary<string, TokenBucket> _buckets = new();

    public ValueTask<RateLimitResult> AcquireAsync(string key, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        var bucket = _buckets.GetOrAdd(key, _ => new TokenBucket(_options, now));

        return ValueTask.FromResult(bucket.TryTake(now));
    }
}
