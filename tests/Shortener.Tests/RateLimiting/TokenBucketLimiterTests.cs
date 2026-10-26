using Microsoft.Extensions.Time.Testing;

namespace Shortener.Tests;

public class TokenBucketLimiterTests
{
    const int Capacity = 3;
    const string Key = "source-ipaddress";

    [Fact]
    public async Task A_new_key_allows_a_burst_of_capacity_then_rejects()
    {
        // each bucket allows 3 tokens, refill 1 token every 10 seconds
        var (rateLimiter, _) = CreateLimiter(Capacity, 1, TimeSpan.FromSeconds(10));
        await DrainAsync(rateLimiter, Key, Capacity);

        var rejected = await rateLimiter.AcquireAsync(Key);
        Assert.False(rejected.IsAllowed, $"Call 4 of capacity {Capacity} should be rejected");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Waiting_retry_after_from_a_partial_token_allows_exactly_one_more_request(
        int advancedSeconds
    )
    {
        // each bucket allows 3 tokens, refill 1 token every 10 seconds. refill rate 0.1
        var (rateLimiter, clock) = CreateLimiter(Capacity, 1, TimeSpan.FromSeconds(10));
        await DrainAsync(rateLimiter, Key, Capacity);

        clock.Advance(TimeSpan.FromSeconds(advancedSeconds));

        var rejected = await rateLimiter.AcquireAsync(Key);
        Assert.False(rejected.IsAllowed);

        var waitingPeriod = rejected.RetryAfter;

        clock.Advance(waitingPeriod);

        var allowed = await rateLimiter.AcquireAsync(Key);
        Assert.True(allowed.IsAllowed);

        // the one refilled token should be used
        rejected = await rateLimiter.AcquireAsync(Key);
        Assert.False(rejected.IsAllowed);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(1, 9)]
    [InlineData(2, 8)]
    [InlineData(3, 7)]
    public async Task A_rejection_retries_after_the_time_to_refill_one_token(
        int advancedSeconds,
        int expectedRetrySeconds
    )
    {
        // each bucket allows 3 tokens, refill 1 token every 10 seconds. refill rate 0.1
        var (rateLimiter, clock) = CreateLimiter(Capacity, 1, TimeSpan.FromSeconds(10));
        await DrainAsync(rateLimiter, Key, Capacity);

        clock.Advance(TimeSpan.FromSeconds(advancedSeconds));

        var rejected = await rateLimiter.AcquireAsync(Key);
        Assert.False(rejected.IsAllowed);

        var waitingPeriod = rejected.RetryAfter;
        Assert.Equal(TimeSpan.FromSeconds(expectedRetrySeconds), waitingPeriod);
    }

    [Fact]
    public async Task Refill_is_capped_at_capacity_after_a_long_idle()
    {
        // each bucket allows 3 tokens, refill 1 token every 10 seconds. refill rate 0.1
        var (rateLimiter, clock) = CreateLimiter(Capacity, 1, TimeSpan.FromSeconds(10));
        await DrainAsync(rateLimiter, Key, Capacity);

        // after a long wait
        clock.Advance(TimeSpan.FromHours(1));
        await DrainAsync(rateLimiter, Key, Capacity);

        // the one refilled token should be used
        var rejected = await rateLimiter.AcquireAsync(Key);
        Assert.False(rejected.IsAllowed);
    }

    [Fact]
    public async Task Keys_have_independent_buckets()
    {
        // each bucket allows 3 tokens, refill 1 token every 10 seconds. refill rate 0.1
        var (rateLimiter, _) = CreateLimiter(Capacity, 1, TimeSpan.FromSeconds(10));
        await DrainAsync(rateLimiter, Key, Capacity);

        // key A failed
        Assert.False((await rateLimiter.AcquireAsync(Key)).IsAllowed);

        // adding a new key B and draining it
        await DrainAsync(rateLimiter, "B", Capacity);

        // key B failed
        Assert.False((await rateLimiter.AcquireAsync("B")).IsAllowed);

        // adding A still failed
        Assert.False((await rateLimiter.AcquireAsync(Key)).IsAllowed);
    }

    [Theory]
    [InlineData(-1, 1, 10, "capacity")]
    [InlineData(0, 1, 10, "capacity")]
    [InlineData(1, -1, 10, "tokensPerPeriod")]
    [InlineData(1, 0, 10, "tokensPerPeriod")]
    [InlineData(1, 1, -1, "period")]
    [InlineData(1, 1, 0, "period")]
    public void Options_reject_non_positive_values(
        int capacity,
        int tokensPerPeriod,
        int periodSeconds,
        string expectedParamName
    )
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TokenBucketOptions(capacity, tokensPerPeriod, TimeSpan.FromSeconds(periodSeconds))
        );
        Assert.Equal(expectedParamName, ex.ParamName);
    }

    [Fact]
    public void Options_accept_boundary_values()
    {
        var options = new TokenBucketOptions(1, 1, TimeSpan.FromSeconds(1));

        Assert.Equal(1, options.Capacity);
        Assert.Equal(1, options.TokensPerPeriod);
        Assert.Equal(TimeSpan.FromSeconds(1), options.Period);
    }

    private static async Task DrainAsync(IRateLimiter rateLimiter, string key, int capacity)
    {
        for (int i = 0; i < capacity; i++)
        {
            var result = await rateLimiter.AcquireAsync(key);
            Assert.True(result.IsAllowed, $"Call {i + 1} of capacity {capacity} should be allowed");
        }
    }

    private static (IRateLimiter RateLimiter, FakeTimeProvider Clock) CreateLimiter(
        int capacity,
        int tokensPerPeriod,
        TimeSpan period
    )
    {
        var options = new TokenBucketOptions(capacity, tokensPerPeriod, period);

        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var tokenBucketLimiter = new TokenBucketLimiter(options, clock);

        return (tokenBucketLimiter, clock);
    }
}
