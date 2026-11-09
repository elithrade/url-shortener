using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Shortener.Tests;

public class ClicksEndpointTests : IClassFixture<SpyClicksFactory>
{
    private readonly SpyClicksFactory _factory;
    private readonly HttpClient _client;

    public ClicksEndpointTests(SpyClicksFactory factory)
    {
        _factory = factory;
        _client = _factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false, // we want to see 302 itself
            }
        );
    }

    [Fact]
    public async Task Redirect_records_a_click()
    {
        var response = await _client.PostAsJsonAsync(
            "/shorten",
            new { url = "https://example.com/path" }
        );

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<ShortenResponse>();
        Assert.NotNull(json);

        var code = json.Code;

        response = await _client.GetAsync(code);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var clickStore = _factory.Store;

        var entries = await clickStore.NextBatchAsync();
        var entry = Assert.Single(entries);

        Assert.Equal(code, entry.Code);
        Assert.Equal(1, entry.Count);
    }

    [Fact]
    public async Task Code_not_found_does_not_record_a_click()
    {
        var response = await _client.GetAsync("abc");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var clickStore = _factory.Store;

        var noBatch = await clickStore.NoBatchWithinAsync(TimeSpan.FromMilliseconds(500));
        Assert.True(noBatch);
    }
}
