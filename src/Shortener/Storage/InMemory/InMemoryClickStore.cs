using System.Collections.Concurrent;

namespace Shortener;

public sealed class InMemoryClickStore : IClickStore
{
    private readonly ConcurrentDictionary<(string Code, DateOnly Day), long> _counts = [];

    public ValueTask AddAsync(IReadOnlyCollection<DailyClicks> clicks, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (clicks.Any(c => c.Count <= 0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(clicks),
                "Count must be greater than zero"
            );
        }

        foreach (var click in clicks)
        {
            _counts.AddOrUpdate(
                key: (click.Code, click.Day),
                addValueFactory: k => click.Count,
                updateValueFactory: (k, old) => old + click.Count
            );
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<long> GetAsync(string code, DateOnly start, DateOnly end, CancellationToken ct)
    {
        if (start > end)
        {
            throw new ArgumentException("start day must not be after end day", nameof(start));
        }

        var sum = _counts
            .Where(kvp => kvp.Key.Day >= start && kvp.Key.Day <= end && kvp.Key.Code == code)
            .Sum(kvp => kvp.Value);

        return ValueTask.FromResult(sum);
    }
}
