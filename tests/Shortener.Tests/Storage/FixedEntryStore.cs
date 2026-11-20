namespace Shortener.Tests;

// Returns the same entry for every code, whatever its ExpiresAt. With an entry that has already
// expired, it stands in for a link that expired while the real store was reading it.
internal sealed class FixedEntryStore(LinkEntry entry) : IUrlStore
{
    public ValueTask<LinkEntry?> GetAsync(string code, CancellationToken ct) => new(entry);

    public ValueTask<bool> TryAddAsync(string code, LinkEntry linkEntry, CancellationToken ct) =>
        new(true);
}
