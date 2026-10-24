using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

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
        var alwaysFailStore = CollidingStore.Always();
        var collidingClient = NewClient(alwaysFailStore);

        var response = await collidingClient.PostAsJsonAsync(
            "/shorten",
            new { url = "https://example.com/path" }
        );

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(5, alwaysFailStore.Attempts);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Succeeds_when_collision_under_limit(int collisions)
    {
        var store = new CollidingStore(collisions);
        var collidingClient = NewClient(store);

        var response = await collidingClient.PostAsJsonAsync(
            "/shorten",
            new { url = "https://example.com/path" }
        );

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var actual = await response.Content.ReadFromJsonAsync<ShortenResponse>();
        var code = actual?.Code;

        Assert.NotNull(code);

        var getResponse = await collidingClient.GetAsync($"/{code}");

        Assert.Equal(HttpStatusCode.Found, getResponse.StatusCode);
        Assert.Equal("https://example.com/path", getResponse.Headers.Location?.OriginalString);
        Assert.Equal(collisions + 1, store.Attempts);
    }

    [Theory]
    [InlineData(4, HttpStatusCode.Created)] // the 5th (last allowed) attempt succeeds
    [InlineData(5, HttpStatusCode.ServiceUnavailable)] // all 5 attempts collide
    public async Task Returns_expected_status_at_the_retry_limit(
        int collisions,
        HttpStatusCode expected
    )
    {
        var store = new CollidingStore(collisions);
        var collidingClient = NewClient(store);

        var response = await collidingClient.PostAsJsonAsync(
            "/shorten",
            new { url = "https://example.com/path" }
        );

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(Math.Min(collisions + 1, 5), store.Attempts);
    }

    [Fact]
    public async Task Concurrent_posts_get_distinct_codes_that_resolve_to_their_own_urls()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var baseUrl = "https://example.com/";

        // simulate starting all 200 tasks at once trying to add to the store
        var tasks = Enumerable
            .Range(0, 200)
            .Select(i =>
                Task.Run(async () =>
                {
                    await gate.Task;

                    var key = $"k{i}";
                    var response = await _client.PostAsJsonAsync(
                        "/shorten",
                        new { url = baseUrl + key }
                    );
                    var status = response.StatusCode;
                    var code = (await response.Content.ReadFromJsonAsync<ShortenResponse>())?.Code;

                    return (i, status, code);
                })
            )
            .ToArray();

        // release and run all
        gate.SetResult();
        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.Equal(HttpStatusCode.Created, r.status));
        Assert.Equal(200, results.Select(r => r.code).Distinct().Count());

        foreach (var (i, _, code) in results)
        {
            var response = await _client.GetAsync($"/{code}");
            var key = $"k{i}";

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal(baseUrl + key, response.Headers.Location?.OriginalString);
        }
    }

    [Theory]
    [InlineData(3600, 3600)]
    [InlineData(null, 30 * 24 * 3600)] // default to 30 days
    public async Task Accept_before_expiry_and_reject_after_expiry(
        int? expiresInSeconds,
        int lifeTimeInSeconds
    )
    {
        DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var (client, clock) = NewFakeTimerClient(now);

        var response = await client.PostAsJsonAsync(
            "/shorten",
            new { Url = "https://example.com/abc", ExpiresInSeconds = expiresInSeconds }
        );
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var code = (await response.Content.ReadFromJsonAsync<ShortenResponse>())?.Code;
        Assert.NotNull(code);

        clock.Advance(TimeSpan.FromSeconds(lifeTimeInSeconds - 1));

        var getResponse = await client.GetAsync($"/{code}");

        Assert.Equal(HttpStatusCode.Redirect, getResponse.StatusCode);
        Assert.Equal("https://example.com/abc", getResponse.Headers.Location?.OriginalString);

        clock.Advance(TimeSpan.FromSeconds(1));

        getResponse = await client.GetAsync($"/{code}");
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    [Theory]
    [InlineData(-5, HttpStatusCode.BadRequest)]
    [InlineData(0, HttpStatusCode.BadRequest)]
    [InlineData(59, HttpStatusCode.BadRequest)] // just under the minimum
    [InlineData(60, HttpStatusCode.Created)]
    [InlineData(31536000, HttpStatusCode.Created)]
    [InlineData(31536001, HttpStatusCode.BadRequest)] // just over the maximum
    public async Task Expiry_boundaries_return_expected_status(
        int expiresInSeconds,
        HttpStatusCode expected
    )
    {
        var response = await _client.PostAsJsonAsync(
            "/shorten",
            new { Url = "https://example.com/abc", ExpiresInSeconds = expiresInSeconds }
        );

        Assert.Equal(expected, response.StatusCode);
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            var error = await response.Content.ReadFromJsonAsync<string>();
            Assert.Contains("31536000", error);
        }
    }

    [Fact]
    public void IUrlStore_and_IExpirySweeper_resolve_the_same_instance()
    {
        var urlStore = _factory.Services.GetRequiredService<IUrlStore>();
        var expirySweeper = _factory.Services.GetRequiredService<IExpirySweeper>();

        Assert.Same(urlStore, expirySweeper);
    }

    [Fact]
    public void Default_should_resolve_InMemoryUrlStore()
    {
        var urlStore = _factory.Services.GetRequiredService<IUrlStore>();
        var expirySweeper = _factory.Services.GetRequiredService<IExpirySweeper>();

        Assert.IsType<InMemoryUrlStore>(urlStore);
        Assert.IsType<InMemoryUrlStore>(expirySweeper);
    }

    [Theory]
    [InlineData("Postgres   ")]
    [InlineData("POSTGRES")]
    [InlineData("Postgres")]
    public void Postgres_storage_without_a_connection_string_fails_at_startup(string value)
    {
        using var factory = _factory.WithWebHostBuilder(b => b.UseSetting("Storage", value));

        var ex = Assert.Throws<InvalidOperationException>(factory.CreateClient);

        Assert.Contains("ConnectionStrings:Shortener", ex.Message);
    }

    [Theory]
    [InlineData("Postgress")]
    [InlineData("abc")]
    [InlineData("5")]
    [InlineData("1")]
    [InlineData("")]
    public void Unknown_storage_type_fails_at_startup(string value)
    {
        using var factory = _factory.WithWebHostBuilder(b => b.UseSetting("Storage", value));

        var ex = Assert.Throws<InvalidOperationException>(factory.CreateClient);

        Assert.Contains("InMemory", ex.Message);
        Assert.Contains("Postgres", ex.Message);
    }

    [Theory]
    [InlineData("InMemory")]
    [InlineData("inmemory")]
    [InlineData("INMEMORY")]
    public void InMemory_in_any_case_selects_the_in_memory_store(string value)
    {
        using var factory = _factory.WithWebHostBuilder(b => b.UseSetting("Storage", value));

        var store = factory.Services.GetRequiredService<IUrlStore>();

        Assert.IsType<InMemoryUrlStore>(store);
    }

    [Fact]
    public async Task Unavailable_store_returns_503_on_post()
    {
        var exceptionStore = new ExceptionStore(
            new StorageUnavailableException(
                "Store is not available",
                new InvalidOperationException("secret:1234")
            )
        );

        var client = NewClient(exceptionStore);
        var response = await client.PostAsJsonAsync(
            "/shorten",
            new { url = "https://example.com/path" }
        );

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        // breaks on the first attempts
        Assert.Equal(1, exceptionStore.Attempts);

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("secret:1234", body);
    }

    [Fact]
    public async Task Unavailable_store_returns_503_on_get()
    {
        var exceptionStore = new ExceptionStore(
            new StorageUnavailableException(
                "Store is not available",
                new InvalidOperationException("secret:1234")
            )
        );

        var client = NewClient(exceptionStore);
        var response = await client.GetAsync("/abc");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("secret:1234", body);
    }

    [Fact]
    public async Task Other_store_exceptions_return_500_on_post()
    {
        var exceptionStore = new ExceptionStore(
            new InvalidOperationException(
                "Missing connection string",
                new InvalidOperationException()
            )
        );

        var client = NewClient(exceptionStore);
        var response = await client.PostAsJsonAsync(
            "/shorten",
            new { url = "https://example.com/path" }
        );

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        // breaks on the first attempts
        Assert.Equal(1, exceptionStore.Attempts);
    }

    [Fact]
    public async Task Other_store_exceptions_return_500_on_get()
    {
        var exceptionStore = new ExceptionStore(
            new ArgumentException("Code is too long", new InvalidOperationException())
        );

        var client = NewClient(exceptionStore);
        var response = await client.GetAsync("/abc");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    private (HttpClient, FakeTimeProvider) NewFakeTimerClient(DateTimeOffset now)
    {
        var clock = new FakeTimeProvider(now);

        var factory = _factory.WithWebHostBuilder(b =>
            b.ConfigureServices(s =>
            {
                s.AddSingleton<TimeProvider>(clock);
            })
        );

        return (
            factory.CreateClient(
                new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }
            ),
            clock
        );
    }

    private HttpClient NewClient(IUrlStore store)
    {
        var factory = _factory.WithWebHostBuilder(b =>
            b.ConfigureServices(s =>
            {
                s.AddSingleton(store);
            })
        );

        return factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }
        );
    }
}
