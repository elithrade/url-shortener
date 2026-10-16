namespace Shortener.Tests;

public class InMemoryStoreTests
{
    [Fact]
    public async Task Store_guarantees_uniqueness_under_contention()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new InMemoryUrlStore();
        var baseUrl = "https://example.com/";

        // simulate starting all 1000 tasks at once trying to add to the store
        var tasks = Enumerable
            .Range(0, 1000)
            .Select(i =>
                Task.Run(async () =>
                {
                    await gate.Task;
                    return await store.TryAddAsync("same", baseUrl + i, CancellationToken.None);
                })
            )
            .ToArray();

        // release all
        gate.SetResult();
        bool[] results = await Task.WhenAll(tasks);

        Assert.Equal(1000, results.Length);
        Assert.Equal(1, results.Count(x => x));

        var winner = results.IndexOf(true);
        Assert.Equal(baseUrl + winner, await store.GetAsync("same", CancellationToken.None));
    }

    [Fact]
    public async Task Distinct_keys_are_all_stored()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new InMemoryUrlStore();
        var baseUrl = "https://example.com/";

        // simulate starting all 1000 tasks at once trying to add to the store
        var tasks = Enumerable
            .Range(0, 1000)
            .Select(i =>
                Task.Run(async () =>
                {
                    await gate.Task;

                    bool allAdded = true;
                    // each task adds unique keys
                    for (int j = 0; j < 10; j++)
                    {
                        var key = $"k{i}-{j}";
                        allAdded &= await store.TryAddAsync(
                            key,
                            baseUrl + key,
                            CancellationToken.None
                        );
                    }

                    return allAdded;
                })
            )
            .ToArray();

        // release and run all
        gate.SetResult();
        bool[] results = await Task.WhenAll(tasks);

        Assert.All(results, Assert.True);

        for (var i = 0; i < 1000; i++)
        {
            for (var j = 0; j < 10; j++)
            {
                var key = $"k{i}-{j}";
                Assert.Equal(baseUrl + key, await store.GetAsync(key, CancellationToken.None));
            }
        }
    }
}
