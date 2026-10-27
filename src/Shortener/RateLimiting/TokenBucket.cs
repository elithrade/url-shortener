namespace Shortener;

internal sealed class TokenBucket(TokenBucketOptions options, DateTimeOffset createdAt)
{
    private readonly TokenBucketOptions _options = options;
    private readonly Lock _lock = new();

    private DateTimeOffset _lastRefill = createdAt;
    private double _tokens = options.Capacity;

    public RateLimitResult TryTake(DateTimeOffset now)
    {
        lock (_lock)
        {
            var elapsed = now - _lastRefill;
            var refillRate = _options.TokenRefillPerSeconds;

            _tokens = Math.Min(_options.Capacity, _tokens + elapsed.TotalSeconds * refillRate);
            _lastRefill = now;

            if (_tokens >= 1)
            {
                _tokens -= 1;
                return RateLimitResult.Allowed;
            }

            var retrySeconds = (1 - _tokens) / refillRate;

            return RateLimitResult.Rejected(
                TimeSpan.FromTicks((long)Math.Ceiling(retrySeconds * TimeSpan.TicksPerSecond))
            );
        }
    }
}
