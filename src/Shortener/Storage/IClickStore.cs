namespace Shortener;

public interface IClickStore
{
    ValueTask AddAsync(IReadOnlyCollection<DailyClicks> clicks, CancellationToken ct);
    ValueTask<long> GetAsync(string code, DateOnly start, DateOnly end, CancellationToken ct);
}

public sealed record DailyClicks(string Code, DateOnly Day, int Count);
