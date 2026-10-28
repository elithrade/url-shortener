using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Shortener.Tests;

public class RateLimitEndpointTests : IClassFixture<StrictRateLimitFactory>
{
    private readonly HttpClient _client;
    private readonly WebApplicationFactory<Program> _factory;

    public RateLimitEndpointTests(StrictRateLimitFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false, // we want to see 302 itself
            }
        );
    }

    [Fact]
    public async Task Request_over_the_limit_get_429()
    {
        const string ip = "1.2.3.4";

        var first = await PostShortenAsync(ip);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Null(first.Headers.RetryAfter);

        var second = await PostShortenAsync(ip);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        // capacity reached
        var third = await PostShortenAsync(ip);
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
    }

    [Fact]
    public async Task A_limited_request_says_when_to_retry()
    {
        const string ip = "5.6.7.8";

        await PostShortenAsync(ip);
        await PostShortenAsync(ip);

        var third = await PostShortenAsync(ip);
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);

        var retryAfter = third.Headers.RetryAfter;

        Assert.NotNull(retryAfter);
        Assert.Equal(TimeSpan.FromSeconds(3600), retryAfter.Delta);
    }

    [Fact]
    public async Task A_client_over_the_limit_does_not_limit_other_clients()
    {
        const string clientOne = "192.168.0.1";
        const string clientTwo = "192.168.0.2";

        await PostShortenAsync(clientOne);
        await PostShortenAsync(clientOne);
        var third = await PostShortenAsync(clientOne);

        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);

        var first = await PostShortenAsync(clientTwo);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
    }

    [Fact]
    // only this test may send requests without an IP, because the Unknown bucket is shared by the whole class
    public async Task Clients_without_an_address_share_one_limit()
    {
        var first = await PostShortenAsync(null);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await PostShortenAsync(null);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        var third = await PostShortenAsync(null);

        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
    }

    [Fact]
    public async Task Redirects_are_not_limited()
    {
        const string ip = "202.101.0.1";

        var response = await PostShortenAsync(ip);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var content = await response.Content.ReadFromJsonAsync<ShortenResponse>();
        var code = content?.Code;
        Assert.NotNull(code);

        for (int i = 0; i < 10; i++)
        {
            var get = await GetAsync(code, ip);
            Assert.Equal(HttpStatusCode.Redirect, get.StatusCode);
        }
    }

    private async Task<HttpResponseMessage> GetAsync(string code, string clientIp)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/{code}");

        if (clientIp is not null)
        {
            request.Headers.Add("X-Test-Client-Ip", clientIp);
        }

        return await _client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> PostShortenAsync(string? clientIp)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/shorten")
        {
            Content = JsonContent.Create(new { url = $"https://example.com/{Guid.NewGuid()}" }),
        };

        if (clientIp is not null)
        {
            request.Headers.Add("X-Test-Client-Ip", clientIp);
        }

        return await _client.SendAsync(request);
    }
}
