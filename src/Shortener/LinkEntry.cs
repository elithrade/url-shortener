namespace Shortener;

public sealed record LinkEntry(string Url, DateTimeOffset? ExpiresAt);
