using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Shortener.Tests;

public class ShortenEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public ShortenEndpointTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false, // we want to see 302 itself
            }
        );
    }

    [Fact]
    public async Task Post_valid_url_json_content_then_redirect_to_original_url()
    {
        var response = await _client.PostAsJsonAsync(
            "/shorten",
            new { url = "https://example.com" }
        );

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<ShortenResponse>();
        Assert.NotNull(body);

        var getResponse = await _client.GetAsync($"/{body.Code}");

        Assert.Equal(HttpStatusCode.Found, getResponse.StatusCode);
        // the validator stores AbsoluteUri which for a bare host is https://example.com/
        Assert.Equal("https://example.com/", getResponse.Headers.Location?.OriginalString);
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

    [Fact]
    public async Task Get_unknown_code_returns_404()
    {
        var response = await _client.GetAsync("/nope");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
