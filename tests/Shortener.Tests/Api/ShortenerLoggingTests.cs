using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace Shortener.Tests;

public class ShortenerLoggingTests(WebApplicationFactory<Program> baseFactory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private const string RequestLogCategory =
        "Microsoft.AspNetCore.HttpLogging.HttpLoggingMiddleware";

    // the app with every log entry captured in memory
    private WebApplicationFactory<Program> CreateFactory() =>
        baseFactory.WithWebHostBuilder(b =>
            b.ConfigureLogging(logging => logging.AddFakeLogging())
        );

    private static List<FakeLogRecord> RequestLogs(FakeLogCollector collector) =>
        [.. collector.GetSnapshot().Where(r => r.Category == RequestLogCategory)];

    [Fact]
    public async Task Post_gives_exactly_one_log_with_expected_fields()
    {
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var response = await client.PostShortenAsync("https://example.com/");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var collector = factory.Services.GetFakeLogCollector();
        var requestLogs = RequestLogs(collector);

        var log = Assert.Single(requestLogs);

        Assert.Equal("/shorten", log.GetStructuredStateValue("Path"));
        Assert.Equal("201", log.GetStructuredStateValue("StatusCode"));
    }

    [Fact]
    public async Task Logs_contains_no_sensitive_data()
    {
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var shorten = await client.PostShortenAsync("https://example.com/p?token=SECRET");
        Assert.Equal(HttpStatusCode.Created, shorten.StatusCode);

        var lookup = await client.GetAsync("/nope?x=SECRET2");
        Assert.Equal(HttpStatusCode.NotFound, lookup.StatusCode);

        var collector = factory.Services.GetFakeLogCollector();

        // privacy: no record anywhere mentions the secret, in its message or its properties
        Assert.DoesNotContain(
            collector.GetSnapshot(),
            r =>
                r.Message.Contains("SECRET")
                || r.StructuredState?.Any(kv => kv.Value?.Contains("SECRET") == true) == true
        );
    }

    [Fact]
    public async Task Logs_are_excluded_for_health_endpoints()
    {
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        await client.PostShortenAsync("https://example.com/");
        await client.GetAsync("/health/live");
        await client.GetAsync("/nope");
        await client.GetAsync("/health/ready");

        var collector = factory.Services.GetFakeLogCollector();
        var requestLogs = RequestLogs(collector);

        // health: no request log for a /health path
        Assert.DoesNotContain(
            requestLogs,
            r => r.GetStructuredStateValue("Path")?.StartsWith("/health") == true
        );
    }
}
