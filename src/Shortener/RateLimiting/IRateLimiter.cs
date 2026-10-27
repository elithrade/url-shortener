namespace Shortener;

public interface IRateLimiter
{
    ValueTask<RateLimitResult> AcquireAsync(string key, CancellationToken ct = default);
}
