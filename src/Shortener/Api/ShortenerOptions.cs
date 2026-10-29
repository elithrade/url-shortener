namespace Shortener;

public sealed class ShortenerOptions
{
    public const string Section = "Shortener";

    // the public address of this service, for example https://host.com
    public string? BaseUrl { get; init; }
}
