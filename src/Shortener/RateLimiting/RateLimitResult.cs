namespace Shortener;

public sealed record RateLimitResult
{
    public bool IsAllowed { get; }
    public TimeSpan RetryAfter { get; }

    public static RateLimitResult Allowed { get; } = new(true, TimeSpan.Zero);

    public static RateLimitResult Rejected(TimeSpan retryAfter)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retryAfter, TimeSpan.Zero);
        return new(false, retryAfter);
    }

    private RateLimitResult(bool isAllowed, TimeSpan retryAfter)
    {
        IsAllowed = isAllowed;
        RetryAfter = retryAfter;
    }
}
