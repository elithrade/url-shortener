namespace Shortener.Tests;

public class TokenBucketLimiterTests
{
    [Fact]
    public async Task A_new_key_allows_a_burst_of_capacity_then_rejects() { }

    [Fact]
    public async Task A_rejection_retries_after_a_full_token() { }

    [Fact]
    public async Task A_rejection_retries_after_the_rest_of_a_partial_token() { }

    [Fact]
    public async Task Waiting_retry_after_allows_exactly_one_more_request() { }

    [Fact]
    public async Task Refill_is_capped_at_capacity_after_a_long_idle() { }

    [Fact]
    public async Task Keys_have_independent_buckets() { }

    [Fact]
    public async Task Options_reject_a_non_positive_capacity() { }
}
