namespace Shortener;

public sealed class LinkRecord
{
    public required string Code { get; set; }
    public required string Url { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }
}
