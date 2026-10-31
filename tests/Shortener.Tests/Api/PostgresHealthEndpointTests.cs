using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Shortener.Tests;

[Collection("postgres")]
public class PostgresHealthEndpointTests(
    WebApplicationFactory<Program> baseFactory,
    PostgresFixture fixture
) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Ready_when_postgres_reachable_returns_200()
    {
        using var factory = baseFactory.WithWebHostBuilder(b =>
            b.UseSetting("Storage", "Postgres")
                .UseSetting("ConnectionStrings:Shortener", fixture.ConnectionString)
        );

        var client = factory.CreateClient();
        var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
