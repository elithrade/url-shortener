namespace Shortener;

public sealed record TokenBucketOptions
{
    public int Capacity { get; }
    public int TokensPerPeriod { get; }
    public TimeSpan Period { get; }

    public TokenBucketOptions(int capacity, int tokensPerPeriod, TimeSpan period)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tokensPerPeriod);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(period, TimeSpan.Zero);

        Capacity = capacity;
        TokensPerPeriod = tokensPerPeriod;
        Period = period;
    }
}
