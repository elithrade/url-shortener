namespace Shortener;

// Per-key state. Mutable on purpose: the limiter changes it in place under a lock.
internal sealed class TokenBucket
{
    public double Tokens { get; set; }
    public DateTimeOffset LastRefill { get; set; }

    public TokenBucket(double tokens, DateTimeOffset lastRefill)
    {
        Tokens = tokens;
        LastRefill = lastRefill;
    }
}
