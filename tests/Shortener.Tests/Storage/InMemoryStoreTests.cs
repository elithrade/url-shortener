using Microsoft.Extensions.Time.Testing;

namespace Shortener.Tests;

public class InMemoryStoreTests
{
    private readonly TimeProvider _fakeTimeProvider = new FakeTimeProvider();

    [Fact]
    public async Task Store_guarantees_uniqueness_under_contention()
    {
        var store = new InMemoryUrlStore(_fakeTimeProvider);
        var baseUrl = "https://example.com/";

        // 1000 tasks released at once, all trying to add the same key
        bool[] results = await Concurrently.RunAsync(
            1000,
            async i =>
                await store.TryAddAsync(
                    "same",
                    new LinkEntry(baseUrl + i, null),
                    CancellationToken.None
                )
        );

        Assert.Equal(1000, results.Length);
        Assert.Equal(1, results.Count(x => x));

        var winner = results.IndexOf(true);
        Assert.Equal(baseUrl + winner, (await store.GetAsync("same", CancellationToken.None))?.Url);
    }

    [Fact]
    public async Task Distinct_keys_are_all_stored()
    {
        var store = new InMemoryUrlStore(_fakeTimeProvider);
        var baseUrl = "https://example.com/";

        // 1000 tasks released at once, each adding its own unique keys
        bool[] results = await Concurrently.RunAsync(
            1000,
            async i =>
            {
                bool allAdded = true;
                for (int j = 0; j < 10; j++)
                {
                    var key = $"k{i}-{j}";
                    var linkEntry = new LinkEntry(baseUrl + key, null);

                    allAdded &= await store.TryAddAsync(key, linkEntry, CancellationToken.None);
                }

                return allAdded;
            }
        );

        Assert.All(results, Assert.True);

        for (var i = 0; i < 1000; i++)
        {
            for (var j = 0; j < 10; j++)
            {
                var key = $"k{i}-{j}";
                Assert.Equal(baseUrl + key, (await store.GetAsync(key, CancellationToken.None))?.Url);
            }
        }
    }
}
