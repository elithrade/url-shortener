using System.Collections.ObjectModel;

namespace Shortener.Tests;

public abstract class ClickStoreContractTests
{
    protected abstract IClickStore CreateStore();
    protected abstract Task AddLinkAsync(string code);

    [Fact]
    public async Task Added_clicks_can_be_read_back()
    {
        const string code = "abc";

        await AddLinkAsync(code);

        var day = new DateOnly(2026, 1, 1);
        var clicks = new[] { new DailyClicks(code, day, 5) };

        var store = CreateStore();
        await store.AddAsync(clicks, CancellationToken.None);

        var count = await store.GetAsync(code, day, day, CancellationToken.None);

        Assert.Equal(5, count);
    }

    [Fact]
    public async Task A_second_add_adds_to_the_first()
    {
        const string code = "abc";
        await AddLinkAsync(code);

        var day = new DateOnly(2026, 1, 1);
        var store = CreateStore();

        // two separate calls on the same code and day: the second must add, not replace
        await store.AddAsync([new DailyClicks(code, day, 5)], CancellationToken.None);
        await store.AddAsync([new DailyClicks(code, day, 10)], CancellationToken.None);

        Assert.Equal(15, await store.GetAsync(code, day, day, CancellationToken.None));
    }

    [Fact]
    public async Task Duplicates_in_one_batch_are_summed()
    {
        await AddLinkAsync("abc");

        var day = new DateOnly(2026, 1, 1);
        var batch = CreateBatch("abc", day, 100, 100);

        var store = CreateStore();
        await store.AddAsync(batch, CancellationToken.None);

        Assert.Equal(100 * 100, await store.GetAsync("abc", day, day, CancellationToken.None));
    }

    [Fact]
    public async Task Days_outside_range_are_not_counted()
    {
        const string code = "abc";
        await AddLinkAsync(code);

        var start = new DateOnly(2026, 1, 2);
        var end = new DateOnly(2026, 1, 3);

        // one day on each side of the range, so an off-by-one at either end shows up
        var store = CreateStore();
        await store.AddAsync(
            [
                new DailyClicks(code, start.AddDays(-1), 100),
                new DailyClicks(code, start, 1),
                new DailyClicks(code, end.AddDays(1), 100),
            ],
            CancellationToken.None
        );

        Assert.Equal(1, await store.GetAsync(code, start, end, CancellationToken.None));
    }

    [Fact]
    public async Task Both_boundary_days_are_counted()
    {
        await AddLinkAsync("abc");

        var first = new DateOnly(2026, 1, 1);
        var firstBatch = CreateBatch("abc", first, 100, 1);

        var second = new DateOnly(2026, 2, 1);
        var secondBatch = CreateBatch("abc", second, 100, 1);

        var store = CreateStore();
        await store.AddAsync(firstBatch, CancellationToken.None);
        await store.AddAsync(secondBatch, CancellationToken.None);

        Assert.Equal(100 + 100, await store.GetAsync("abc", first, second, CancellationToken.None));
    }

    [Fact]
    public async Task Other_codes_are_not_touched()
    {
        const string first = "abc";
        await AddLinkAsync(first);

        const string second = "def";
        await AddLinkAsync(second);

        var day = new DateOnly(2026, 1, 1);
        var store = CreateStore();

        await store.AddAsync([new DailyClicks(first, day, 5)], CancellationToken.None);
        await store.AddAsync([new DailyClicks(second, day, 6)], CancellationToken.None);

        var count = await store.GetAsync(first, day, day, CancellationToken.None);
        Assert.Equal(5, count);

        count = await store.GetAsync(second, day, day, CancellationToken.None);
        Assert.Equal(6, count);
    }

    [Fact]
    public async Task An_unknown_code_returns_zero()
    {
        const string first = "abc";
        await AddLinkAsync(first);

        const string second = "def";
        await AddLinkAsync(second);

        var day = new DateOnly(2026, 1, 1);
        var store = CreateStore();

        await store.AddAsync([new DailyClicks(first, day, 5)], CancellationToken.None);
        await store.AddAsync([new DailyClicks(second, day, 6)], CancellationToken.None);

        var count = await store.GetAsync("ghi", day, day, CancellationToken.None);
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Start_after_end_throws()
    {
        var store = CreateStore();
        var start = new DateOnly(2026, 2, 2);
        var end = new DateOnly(2026, 2, 1);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await store.GetAsync("abc", start, end, CancellationToken.None)
        );
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_count_of_zero_or_less_throws(int count)
    {
        var store = CreateStore();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await store.AddAsync(
                [new DailyClicks("abc", new DateOnly(2026, 1, 1), count)],
                CancellationToken.None
            )
        );
    }

    [Fact]
    public async Task An_empty_batch_does_nothing()
    {
        await AddLinkAsync("abc");

        var store = CreateStore();
        var day = new DateOnly(2026, 1, 1);

        await store.AddAsync([new DailyClicks("abc", day, 1)], CancellationToken.None);
        await store.AddAsync([], CancellationToken.None);

        Assert.Equal(1, await store.GetAsync("abc", day, day, CancellationToken.None));
    }

    [Fact]
    public async Task Concurrent_adds_lose_nothing()
    {
        var store = CreateStore();

        const string code = "abc";
        await AddLinkAsync(code);

        const int batchSize = 10;
        const int countPerAdd = 10;
        const int taskCount = 1000;

        var day = new DateOnly(2026, 1, 1);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable
            .Range(0, taskCount)
            .Select(_ =>
                Task.Run(async () =>
                {
                    await gate.Task;
                    await store.AddAsync(
                        CreateBatch(code, day, batchSize, countPerAdd),
                        CancellationToken.None
                    );
                })
            )
            .ToArray();

        // release all
        gate.SetResult();
        await Task.WhenAll(tasks);

        var expected = taskCount * batchSize * countPerAdd;
        Assert.Equal(expected, await store.GetAsync(code, day, day, CancellationToken.None));
    }

    [Fact]
    public async Task Single_day_range_returns_count()
    {
        const string code = "abc";
        await AddLinkAsync(code);

        var day = new DateOnly(2026, 1, 1);
        var batch = CreateBatch(code, day, 100, 10);

        var store = CreateStore();
        await store.AddAsync(batch, CancellationToken.None);
        Assert.Equal(100 * 10, await store.GetAsync("abc", day, day, CancellationToken.None));
    }

    [Fact]
    public async Task Totals_bigger_than_int_are_handled()
    {
        const string code = "abc";
        await AddLinkAsync(code);

        var day = new DateOnly(2026, 1, 1);
        var batch = CreateBatch(code, day, 2, int.MaxValue);

        var store = CreateStore();
        await store.AddAsync(batch, CancellationToken.None);

        Assert.Equal(
            int.MaxValue * 2L,
            await store.GetAsync(code, day, day, CancellationToken.None)
        );
    }

    [Fact]
    public async Task A_batch_with_one_invalid_entry_writes_nothing()
    {
        const string code = "abc";
        var store = CreateStore();
        await AddLinkAsync(code);

        var day = new DateOnly(2026, 1, 1);
        var batch = CreateBatch(code, day, 99, 10);
        batch.Add(new DailyClicks(code, day, -1));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await store.AddAsync(batch, CancellationToken.None)
        );
        Assert.Equal(0, await store.GetAsync(code, day, day, CancellationToken.None));
    }

    private static List<DailyClicks> CreateBatch(
        string code,
        DateOnly day,
        int batchSize,
        int countPerAdd
    )
    {
        var result = new List<DailyClicks>();
        for (int i = 0; i < batchSize; i++)
        {
            var click = new DailyClicks(code, day, countPerAdd);

            result.Add(click);
        }

        return result;
    }
}
