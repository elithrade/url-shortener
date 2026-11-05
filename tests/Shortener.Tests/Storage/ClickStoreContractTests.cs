namespace Shortener.Tests;

public abstract class ClickStoreContractTests
{
    protected abstract IClickStore CreateStore();
    protected abstract Task AddLinkAsync(string code);

    [Fact]
    public async Task Added_clicks_can_be_read_back()
    {
        const string code = "abc";
        var day = new DateOnly(2026, 1, 1);

        var store = CreateStore();
        await AddLinkAsync(code);

        var clicks = new[] { new DailyClicks(code, day, 5) };
        await store.AddAsync(clicks, CancellationToken.None);

        var count = await store.GetAsync(code, day, day, CancellationToken.None);

        Assert.Equal(5, count);
    }

    [Fact(Skip = "todo")]
    public async Task A_second_add_adds_to_the_first() { }

    [Fact(Skip = "todo")]
    public async Task Duplicates_in_one_batch_are_summed() { }

    [Fact(Skip = "todo")]
    public async Task Days_outside_range_are_not_counted() { }

    [Fact(Skip = "todo")]
    public async Task Both_boundary_days_are_counted() { }

    [Fact(Skip = "todo")]
    public async Task Other_codes_are_not_touched() { }

    [Fact(Skip = "todo")]
    public async Task An_unknown_code_returns_zero() { }

    [Fact(Skip = "todo")]
    public async Task Start_after_end_throws() { }

    [Theory(Skip = "todo")]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_count_of_zero_or_less_throws(int count) { }

    [Fact(Skip = "todo")]
    public async Task An_empty_batch_does_nothing() { }

    [Fact(Skip = "todo")]
    public async Task Concurrent_adds_lose_nothing() { }

    [Fact(Skip = "todo")]
    public async Task Single_day_range_returns_count() { }

    [Fact(Skip = "todo")]
    public async Task Totals_bigger_than_int_are_handled() { }

    [Fact(Skip = "todo")]
    public async Task A_batch_with_one_invalid_entry_writes_nothing() { }
}
