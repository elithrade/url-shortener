using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Shortener.Tests;

// The health endpoints while the real app warms up. Warm-up is held open by a store that holds
// lookups of WarmUpService.Code until the test releases them and passes every other call to an
// in-memory store, so real links still work. Each test builds its own app through
// WithWebHostBuilder; disposing it stops the app, which cancels the held lookups.
public class WarmUpEndpointTests(WebApplicationFactory<Program> baseFactory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Ready_is_503_while_warm_up_runs()
    {
        var gate = new GatedUrlStore();
        using var factory = CreateFactory(gate);
        var client = factory.CreateClient();
        await gate.WaitForArrivalsAsync(1);

        var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Ready_is_200_after_warm_up_finishes()
    {
        var gate = new GatedUrlStore();
        using var factory = CreateFactory(gate);
        var client = factory.CreateClient();
        await gate.WaitForArrivalsAsync(1);

        gate.Release();
        await WarmUp(factory).Ready.WaitAsync(Limit);
        var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // the process is fine while warming, so liveness must not fail (it would get it restarted)
    [Fact]
    public async Task Live_is_200_while_warm_up_runs()
    {
        var gate = new GatedUrlStore();
        using var factory = CreateFactory(gate);
        var client = factory.CreateClient();
        await gate.WaitForArrivalsAsync(1);

        var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // With Calls 0 readiness must not depend on the store: the gate stays closed, and warm-up
    // still completes and the app reports ready. (Ready completes on a background thread, so
    // the test waits for it rather than racing the first request.)
    [Fact]
    public async Task Ready_is_200_at_once_when_warm_up_is_off()
    {
        var gate = new GatedUrlStore();
        using var factory = CreateFactory(gate, calls: 0);
        var client = factory.CreateClient();

        await WarmUp(factory).Ready.WaitAsync(Limit);
        var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, gate.MaxInFlight);
    }

    // warm-up only delays readiness; a request that arrives early is still answered
    [Fact]
    public async Task Redirects_work_while_warm_up_runs()
    {
        var gate = new GatedUrlStore();
        using var factory = CreateFactory(gate);
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        await gate.WaitForArrivalsAsync(1);

        var url = ShortenClientExtensions.UniqueUrl();
        var shortened = await client.PostShortenAsync(url);
        Assert.Equal(HttpStatusCode.Created, shortened.StatusCode);
        var body = await shortened.Content.ReadFromJsonAsync<ShortenResponse>();
        Assert.NotNull(body);

        var response = await client.GetAsync($"/{body.Code}");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal(url, response.Headers.Location?.AbsoluteUri);
        Assert.False(WarmUp(factory).Ready.IsCompleted);
    }

    private WebApplicationFactory<Program> CreateFactory(GatedUrlStore gate, int calls = 3) =>
        baseFactory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Storage", "InMemory");
            b.UseSetting("WarmUp:Calls", calls.ToString());
            b.UseSetting("WarmUp:Parallelism", "1");
            b.ConfigureServices(s =>
                s.AddSingleton<IUrlStore>(sp => new HoldWarmUpStore(
                    gate,
                    sp.GetRequiredService<InMemoryUrlStore>()
                ))
            );
        });

    // the running service, however Program.cs registers it
    private static WarmUpService WarmUp(WebApplicationFactory<Program> factory) =>
        factory.Services.GetServices<IHostedService>().OfType<WarmUpService>().Single();

    // holds warm-up lookups at the gate and passes every other call to the real store
    private sealed class HoldWarmUpStore(GatedUrlStore gate, IUrlStore inner) : IUrlStore
    {
        public ValueTask<LinkEntry?> GetAsync(string code, CancellationToken ct) =>
            code == WarmUpService.Code ? gate.GetAsync(code, ct) : inner.GetAsync(code, ct);

        public ValueTask<bool> TryAddAsync(
            string code,
            LinkEntry linkEntry,
            CancellationToken ct
        ) => inner.TryAddAsync(code, linkEntry, ct);
    }
}
