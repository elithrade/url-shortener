namespace Shortener;

public record ShortenedUrlRequest(string Url, int? ExpiresInSeconds);
