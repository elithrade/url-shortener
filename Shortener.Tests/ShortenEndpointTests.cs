using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Shortener.Tests;

public class ShortenEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;
    private readonly WebApplicationFactory<Program> _factory;

    public ShortenEndpointTests(WebApplicationFactory<Program> factory)
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
    public async Task Post_valid_url_string_content_then_redirect_to_original_url()
    {
        var content = new StringContent(
            """{"url":"https://example.com/path?x=1"}""",
            Encoding.UTF8,
            "application/json"
        );

        var response = await _client.PostAsync("/shorten", content);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<ShortenResponse>();
        Assert.NotNull(body);

        var getResponse = await _client.GetAsync($"/{body.Code}");

        Assert.Equal(HttpStatusCode.Found, getResponse.StatusCode);
        Assert.Equal("https://example.com/path?x=1", getResponse.Headers.Location?.OriginalString);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"url":null}""")]
    [InlineData("""{"url":""")]
    public async Task Malformed_json_body_returns_400(string json)
    {
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _client.PostAsync("/shorten", content);

        Assert.True(
            response.StatusCode == HttpStatusCode.BadRequest,
            $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}"
        );
    }

    [Fact]
    public async Task Get_unknown_code_returns_404()
    {
        var response = await _client.GetAsync("/nope");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("foo", "must be valid")]
    [InlineData("javascript:alert(1)", "scheme")]
    [InlineData("http://localhost/x", "own host")]
    [InlineData("http://localhost:5000/x", "own host")]
    [InlineData("", "is required")]
    [InlineData("          ", "is required")]
    [InlineData(null, "is required")]
    public async Task Bad_urls_return_400(string? url, string expectedError)
    {
        var response = await _client.PostAsJsonAsync("/shorten", new { url });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(expectedError, await response.Content.ReadFromJsonAsync<string>());
    }

    [Fact]
    public async Task Long_url_should_return_400()
    {
        string url = "https://example.com/" + new string('a', 3000);
        var response = await _client.PostAsJsonAsync("/shorten", new { url });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("too long", await response.Content.ReadFromJsonAsync<string>());
    }

    [Theory]
    [InlineData("HTTPS://Example.COM/A", "https://example.com/A")]
    [InlineData("HTTPS://example.com:443/a", "https://example.com/a")]
    [InlineData("http://example.com:80/a", "http://example.com/a")] // :80 is the default for http
    [InlineData("HTTPS://example.com:80/a", "https://example.com:80/a")] // :80 is NOT the default for https, so it stays
    public async Task Stores_canonical_url(string url, string expected)
    {
        var response = await _client.PostAsJsonAsync("/shorten", new { url });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var actual = await response.Content.ReadFromJsonAsync<ShortenResponse>();
        var code = actual?.Code;

        Assert.NotNull(code);

        var getResponse = await _client.GetAsync($"/{code}");

        Assert.Equal(HttpStatusCode.Found, getResponse.StatusCode);
        Assert.Equal(expected, getResponse.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Returns_503_after_max_attempts_when_every_code_collide()
    {
        var store = new AlwaysCollidingStore();
        var factory = _factory.WithWebHostBuilder(b =>
            b.ConfigureServices(s =>
            {
                s.AddSingleton<IUrlStore>(store);
            })
        );
        var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }
        );

        var response = await client.PostAsJsonAsync(
            "/shorten",
            new { url = "https://example.com/path" }
        );

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(5, store.Attempts);
    }
}
