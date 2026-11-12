using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;

namespace Shortener.Tests;

// End to end: the app with ConnectionStrings:Redis set. These prove the registration
// (RegisterCache), which the store's own tests never touch.
[Collection("redis")]
public class CacheEndpointTests(RedisFixture redis, WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Url = "https://example.com/path";

    [Fact]
    public async Task Redirects_are_served_through_the_cache()
    {
        var (app, client, prefix) = CreateApp(redis.ConnectionString);
        var reads = CacheReads(app);
        var code = await PostShortenGetCodeAsync(client);

        HttpResponseMessage response = await client.GetAsync(code);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(Url, response.Headers.Location?.ToString());

        response = await client.GetAsync(code);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(Url, response.Headers.Location?.ToString());

        Assert.True(await redis.Redis.GetDatabase().KeyExistsAsync(prefix + code));
        Assert.Equal(1, Count(reads, "miss"));
        Assert.Equal(1, Count(reads, "hit"));
    }

    [Fact]
    public async Task The_app_serves_redirects_when_redis_is_down()
    {
        var (app, client, _) = CreateApp("localhost:1");
        var reads = CacheReads(app);

        string code = await PostShortenGetCodeAsync(client);
        var response = await client.GetAsync(code);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(Url, response.Headers.Location?.ToString());

        Assert.Equal(1, Count(reads, "error"));
        Assert.Equal(0, Count(reads, "hit"));
    }

    [Fact]
    public void Without_a_redis_connection_string_there_is_no_cache()
    {
        var store = factory.Services.GetRequiredService<IUrlStore>();
        Assert.IsNotType<CachedUrlStore>(store);

        var (app, _, _) = CreateApp(redis.ConnectionString);
        store = app.Services.GetRequiredService<IUrlStore>();
        Assert.IsType<CachedUrlStore>(store);
    }

    // the app with the cache switched on, and its own key prefix
    private (WebApplicationFactory<Program> App, HttpClient Client, string Prefix) CreateApp(
        string redisConnectionString
    )
    {
        var prefix = RedisFixture.UniquePrefix();
        var app = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Redis", redisConnectionString);
            b.UseSetting("Cache:KeyPrefix", prefix);
        });
        var client = app.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }
        );

        return (app, client, prefix);
    }

    // collects the app's cache read counter; create it before the requests it should see
    private static MetricCollector<long> CacheReads(WebApplicationFactory<Program> app) =>
        new(
            app.Services.GetRequiredService<IMeterFactory>(),
            CachedUrlStore.MeterName,
            CachedUrlStore.ReadsCounterName
        );

    // how many reads were counted with this result tag
    private static long Count(MetricCollector<long> reads, string result) =>
        reads
            .GetMeasurementSnapshot()
            .Where(m => (string?)m.Tags["result"] == result)
            .Sum(m => m.Value);

    private static async Task<string> PostShortenGetCodeAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/shorten", new { url = Url });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<ShortenResponse>();
        Assert.NotNull(json);

        var code = json.Code;

        return code;
    }
}
