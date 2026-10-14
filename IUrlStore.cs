namespace Shortener;

public interface IUrlStore
{
    ValueTask<bool> TryAddAsync(string code, string url, CancellationToken ct);
    ValueTask<string?> GetAsync(string code, CancellationToken ct);
}
