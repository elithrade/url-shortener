namespace Shortener;

public sealed class TokenBucketLimiter(TokenBucketOptions options, TimeProvider clock)
    : IRateLimiter
{
    private readonly TokenBucketOptions _options = options;
    private readonly TimeProvider clock = clock;

    public RateLimitResult Acquire(string key)
    {
        throw new NotImplementedException();
    }

    public ValueTask<RateLimitResult> AcquireAsync(string key, CancellationToken ct = default)
    {
        throw new NotImplementedException();
    }
}
