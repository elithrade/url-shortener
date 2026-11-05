namespace Shortener;

public sealed class InMemoryClickStore : IClickStore
{
    public ValueTask AddAsync(IReadOnlyCollection<DailyClicks> clicks, CancellationToken ct)
    {
        throw new NotImplementedException();
    }

    public ValueTask<long> GetAsync(string code, DateOnly start, DateOnly end, CancellationToken ct)
    {
        throw new NotImplementedException();
    }
}
