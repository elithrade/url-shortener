using System.Net.Http.Json;

namespace Shortener.Tests;

public static class ShortenClientExtensions
{
    // a URL no other test uses, because the in-memory store is shared within a test class
    public static string UniqueUrl() => $"https://example.com/{Guid.NewGuid()}";

    // POST /shorten with a JSON body. host overrides the Host header (e.g. to forge it),
    // peer sets the address connected to the app, forwardedFor sets X-Forwarded-For
    public static Task<HttpResponseMessage> PostShortenAsync(
        this HttpClient client,
        string url,
        string? host = null,
        string? peer = null,
        string? forwardedFor = null
    )
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/shorten")
        {
            Content = JsonContent.Create(new { url }),
        };

        if (host is not null)
        {
            request.Headers.Host = host;
        }

        if (peer is not null)
        {
            request.Headers.Add("X-Test-Client-Ip", peer);
        }

        if (forwardedFor is not null)
        {
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        }

        return client.SendAsync(request);
    }
}
