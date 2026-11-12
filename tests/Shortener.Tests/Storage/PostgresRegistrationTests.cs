using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

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
    public async Task Request_roundtrip_works_with_migrated_database_with_schema()
    {
        await using var db = await fixture.CreateEmptyDatabaseAsync();
        var exitCode = await SchemaMigrator.ExecuteAsync(db.Factory, NullLogger.Instance);
        Assert.Equal(0, exitCode);

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

    // The production combination. Redis points at a port where nothing listens, so no Redis
    // container is needed: the redirect goes through the decorator, the cache read fails, and
    // Postgres answers.
    [Fact]
    public async Task With_a_cache_the_decorator_wraps_the_postgres_store()
    {
        await using var db = await fixture.CreateEmptyDatabaseAsync();
        Assert.Equal(0, await SchemaMigrator.ExecuteAsync(db.Factory, NullLogger.Instance));

        await using var factory = CreateFactory(db.ConnectionString)
            .WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:Redis", "localhost:1"));

        Assert.IsType<CachedUrlStore>(factory.Services.GetRequiredService<IUrlStore>());
        // the sweeper bypasses the cache
        Assert.IsType<PostgresUrlStore>(factory.Services.GetRequiredService<IExpirySweeper>());

        var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }
        );
        var response = await client.PostAsJsonAsync(
            "/shorten",
            new { url = "https://example.com/cached-postgres" }
        );
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ShortenResponse>();
        Assert.NotNull(body);

        var getResponse = await client.GetAsync($"/{body.Code}");

        Assert.Equal(HttpStatusCode.Found, getResponse.StatusCode);
        Assert.Equal(
            "https://example.com/cached-postgres",
            getResponse.Headers.Location?.OriginalString
        );
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
