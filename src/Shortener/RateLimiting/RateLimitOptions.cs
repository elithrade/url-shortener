using System.ComponentModel.DataAnnotations;

namespace Shortener;

// bound from the RateLimit section, then mapped to TokenBucketOptions
public sealed class RateLimitOptions
{
    public const string Section = "RateLimit";

    [Range(1, int.MaxValue)]
    public int Capacity { get; init; }

    [Range(1, int.MaxValue)]
    public int TokensPerPeriod { get; init; }

    [Range(1, int.MaxValue)]
    public int PeriodSeconds { get; init; }

    public TokenBucketOptions ToTokenBucketOptions() =>
        new(Capacity, TokensPerPeriod, TimeSpan.FromSeconds(PeriodSeconds));
}
