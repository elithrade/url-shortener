using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Shortener.Tests;

public class StatsEndpointTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client = factory.CreateClient(
        new WebApplicationFactoryClientOptions()
    );
    private readonly WebApplicationFactory<Program> _factory = factory;

    private static readonly DateTimeOffset Now = new(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Unavailable_store_returns_503_on_stats()
    {
        var store = new ExceptionClickStore(
            new StorageUnavailableException(
                "Store is not available",
                new InvalidOperationException("secret:1234")
            )
        );
        var client = _factory
            .WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IClickStore>(store)))
            .CreateClient();

        var response = await client.GetAsync("/abc/stats");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("secret:1234", body);
    }

    [Fact]
    public async Task Get_stats_for_a_code_returns_click_count()
    {
        var (code, client) = await CreateLinkWithClicksAsync(
            _factory,
            $"https://example.com/{Guid.NewGuid()}"
        );

        var statsResponse = await client.GetAsync($"/{code}/stats?start=2026-10-01&end=2026-10-04");

        Assert.Equal(HttpStatusCode.OK, statsResponse.StatusCode);

        var stats = await statsResponse.Content.ReadFromJsonAsync<StatsResponse>();

        Assert.NotNull(stats);
        Assert.Equal(new StatsResponse(code, new(2026, 10, 1), new(2026, 10, 4), 5), stats);
        Assert.Equal(5, stats.Clicks);
    }

    [Fact]
    public async Task Get_stats_for_a_non_existing_code_returns_zero()
    {
        var statsResponse = await _client.GetAsync($"/abc/stats?start=2026-10-01&end=2026-10-04");
        var stats = await statsResponse.Content.ReadFromJsonAsync<StatsResponse>();

        Assert.NotNull(stats);
        Assert.Equal(0, stats.Clicks);
    }

    [Theory]
    [InlineData("", "2026-09-03", "2026-10-02", 5)] // both defaulted
    [InlineData("?start=2026-10-01", "2026-10-01", "2026-10-02", 5)] // end defaults to today
    [InlineData("?end=2026-09-30", "2026-09-01", "2026-09-30", 0)] // start = end - 29
    [InlineData("?start=2025-10-02&end=2026-10-02", "2025-10-02", "2026-10-02", 5)] // exactly one year
    public async Task Stats_range_resolves(
        string query,
        string expectedStart,
        string expectedEnd,
        long expectedClicks
    )
    {
        var now = new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);
        var factory = CreateFactoryAt(now);

        var (code, client) = await CreateLinkWithClicksAsync(
            factory,
            $"https://example.com/{Guid.NewGuid()}"
        );

        var statsResponse = await client.GetAsync($"/{code}/stats{query}");

        Assert.Equal(HttpStatusCode.OK, statsResponse.StatusCode);

        var stats = await statsResponse.Content.ReadFromJsonAsync<StatsResponse>();

        Assert.NotNull(stats);
        Assert.Equal(
            new StatsResponse(
                code,
                DateOnly.Parse(expectedStart),
                DateOnly.Parse(expectedEnd),
                expectedClicks
            ),
            stats
        );
    }

    [Theory]
    [InlineData("?start=2026-10-05", "after end")] // start after today
    [InlineData("?start=2026-10-04&end=2026-10-03", "after end")] // start after end
    [InlineData("?start=2026-09-04&end=2026-09-03", "after end")] // start after end
    [InlineData("?start=2025-10-01&end=2026-10-02", "one year")] // one year plus one day
    public async Task Stats_rejects_invalid_range(string query, string expectedError)
    {
        var factory = CreateFactoryAt(Now);
        var client = factory.CreateClient();

        var response = await client.GetAsync($"/abc/stats{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(expectedError, await response.Content.ReadFromJsonAsync<string>());
    }

    private static async Task<(string Code, HttpClient Client)> CreateLinkWithClicksAsync(
        WebApplicationFactory<Program> factory,
        string url
    )
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/shorten", new { url });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<ShortenResponse>();
        Assert.NotNull(json);

        var code = json.Code;
        var clicks = factory.Services.GetRequiredService<IClickStore>();
        await clicks.AddAsync(
            [
                new DailyClicks(code, new(2026, 10, 1), 2),
                new DailyClicks(code, new(2026, 10, 2), 3),
                new DailyClicks(code, new(2026, 10, 5), 4),
            ],
            CancellationToken.None
        );

        return (code, client);
    }

    private WebApplicationFactory<Program> CreateFactoryAt(DateTimeOffset now)
    {
        var clock = new FakeTimeProvider(now);

        var factory = _factory.WithWebHostBuilder(b =>
            b.ConfigureServices(s =>
            {
                s.AddSingleton<TimeProvider>(clock);
            })
        );

        return factory;
    }
}
