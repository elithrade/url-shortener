namespace Shortener.Tests;

// an IClickStore whose every call throws the given exception
internal sealed class ExceptionClickStore(Exception ex) : IClickStore
{
    public ValueTask AddAsync(IReadOnlyCollection<DailyClicks> clicks, CancellationToken ct) =>
        throw ex;

    public ValueTask<long> GetAsync(
        string code,
        DateOnly start,
        DateOnly end,
        CancellationToken ct
    ) => throw ex;
}
