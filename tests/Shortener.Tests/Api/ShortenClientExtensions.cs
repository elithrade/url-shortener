using System.Net.Http.Json;

namespace Shortener.Tests;

public static class ShortenClientExtensions
{
    // POST /shorten with a JSON body; host overrides the Host header (e.g. to forge it)
    public static Task<HttpResponseMessage> PostShortenAsync(
        this HttpClient client,
        string url,
        string? host = null
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

        return client.SendAsync(request);
    }
}
