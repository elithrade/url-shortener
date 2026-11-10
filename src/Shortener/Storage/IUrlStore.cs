namespace Shortener;

public interface IUrlStore
{
    ValueTask<bool> TryAddAsync(string code, LinkEntry linkEntry, CancellationToken ct);
    // the link, or null when the code is unknown or expired
    ValueTask<LinkEntry?> GetAsync(string code, CancellationToken ct);
}
