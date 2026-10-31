using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Shortener.Tests;

public class HealthEndpointTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private const string unreachableConnectionString =
        "Host=127.0.0.1;Port=1;Username=x;Password=x;Database=x;Timeout=5";

    private readonly HttpClient _client = factory.CreateClient();
    private readonly WebApplicationFactory<Program> _factory = factory;

    [Fact]
    public async Task Alive_returns_200()
    {
        var response = await _client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Always_ready_with_in_memory_store_returns_200()
    {
        using var factory = _factory.WithWebHostBuilder(b => b.UseSetting("Storage", "InMemory"));
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Alive_when_postgres_unreachable_returns_200()
    {
        using var factory = _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Storage", "Postgres");
            b.UseSetting("ConnectionStrings:Shortener", unreachableConnectionString);
        });

        var client = factory.CreateClient();

        var response = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Not_ready_when_postgres_unreachable_returns_503()
    {
        using var factory = _factory.WithWebHostBuilder(b =>
            b.UseSetting("Storage", "Postgres")
                .UseSetting("ConnectionStrings:Shortener", unreachableConnectionString)
        );

        var client = factory.CreateClient();
        var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }
}
