namespace Shortener;

public interface IUrlStore
{
    ValueTask<bool> TryAddAsync(string code, LinkEntry linkEntry, CancellationToken ct);
    ValueTask<string?> GetAsync(string code, CancellationToken ct);
}
