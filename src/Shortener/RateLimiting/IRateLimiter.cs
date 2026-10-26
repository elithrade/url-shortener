namespace Shortener;

public interface IRateLimiter
{
    RateLimitResult Acquire(string key);

    ValueTask<RateLimitResult> AcquireAsync(string key, CancellationToken ct = default);
}
