using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Shortener.Tests;

[Collection("postgres")]
public class PostgresRegistrationTests(
    PostgresFixture fixture,
    WebApplicationFactory<Program> baseFactory
) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public void IUrlStore_and_IExpirySweeper_resolve_to_the_same_PostgresUrlStore()
    {
        using var factory = CreateFactory(fixture.ConnectionString);

        var urlStore = factory.Services.GetRequiredService<IUrlStore>();
        var expirySweeper = factory.Services.GetRequiredService<IExpirySweeper>();

        Assert.IsType<PostgresUrlStore>(urlStore);
        Assert.IsType<PostgresUrlStore>(expirySweeper);
        Assert.Same(urlStore, expirySweeper);
    }

    [Fact]
    public void TimeProvider_and_ExpirySweepService_are_registered()
    {
        using var factory = CreateFactory(fixture.ConnectionString);

        var timeProvider = factory.Services.GetRequiredService<TimeProvider>();
        var hostedServices = factory.Services.GetServices<IHostedService>();

        Assert.NotNull(timeProvider);
        Assert.Single(hostedServices.OfType<ExpirySweepService>());
    }

    [Fact]
    public async Task Request_roundtrip_works_with_empty_database_with_no_schema()
    {
        // the test checks EnsureCreatedAsync is called on a fresh new db
        await using var db = await fixture.CreateEmptyDatabaseAsync();
        await using var factory = CreateFactory(db.ConnectionString);

        var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }
        );

        var response = await client.PostAsJsonAsync(
            "/shorten",
            new { url = "https://example.com/path" }
        );

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var actual = await response.Content.ReadFromJsonAsync<ShortenResponse>();
        var code = actual?.Code;

        Assert.NotNull(code);

        var getResponse = await client.GetAsync($"/{code}");

        Assert.Equal(HttpStatusCode.Found, getResponse.StatusCode);
        Assert.Equal("https://example.com/path", getResponse.Headers.Location?.OriginalString);
    }

    private WebApplicationFactory<Program> CreateFactory(string connectionString)
    {
        var factory = baseFactory.WithWebHostBuilder(b =>
            b.UseSetting("Storage", "Postgres")
                .UseSetting("ConnectionStrings:Shortener", connectionString)
        );
        return factory;
    }
}
