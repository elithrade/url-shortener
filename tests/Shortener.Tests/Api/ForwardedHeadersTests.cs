using System.Net;
using Microsoft.Extensions.Options;

namespace Shortener.Tests;

public class ForwardedHeadersTests(StrictRateLimitFactory baseFactory)
    : IClassFixture<StrictRateLimitFactory>
{
    private const string Proxy = "10.0.0.1"; // the peer: what the ingress looks like to the app

    // every request arrives from the proxy; with the proxy trusted, the rate limiter keys on the
    // client in X-Forwarded-For, so one client using up its limit doesn't block another
    [Fact]
    public async Task Clients_behind_the_proxy_get_their_own_limit()
    {
        var client = ClientWith(
            ("ForwardedHeaders:Enabled", "true"),
            ("ForwardedHeaders:KnownNetworks:0", "10.0.0.0/24")
        );

        await DrainAsync(client, "203.0.113.1");

        // proxy forwarded for another client, expect a new bucket
        var response = await client.PostShortenAsync(
            ShortenClientExtensions.UniqueUrl(),
            peer: Proxy,
            forwardedFor: "203.0.113.2"
        );
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    // a client can write anything into X-Forwarded-For, and the proxy only appends to it;
    // reading just the rightmost entry (ForwardLimit 1) means a forged address can't buy a fresh bucket
    [Fact]
    public async Task A_prepended_fake_address_is_ignored()
    {
        var client = ClientWith(
            ("ForwardedHeaders:Enabled", "true"),
            ("ForwardedHeaders:TrustAnyProxy", "true")
        );

        await DrainAsync(client, "203.0.113.1");

        var response = await client.PostShortenAsync(
            ShortenClientExtensions.UniqueUrl(),
            peer: Proxy,
            forwardedFor: "198.51.100.9, 203.0.113.1"
        );
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    // without config the header means nothing: every request counts against the peer (the proxy),
    // so local runs and Compose behave as before
    [Fact]
    public async Task Forwarded_for_is_ignored_by_default()
    {
        var client = ClientWith();
        await DrainAsync(client, "203.0.113.1");

        var response = await client.PostShortenAsync(
            ShortenClientExtensions.UniqueUrl(),
            peer: Proxy,
            forwardedFor: "203.0.113.2"
        );
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    // a request that reaches the app around the proxy (a peer outside KnownNetworks) is limited by
    // its own address, whatever X-Forwarded-For says
    [Fact]
    public async Task Forwarded_for_from_an_untrusted_peer_is_ignored()
    {
        var client = ClientWith(
            ("ForwardedHeaders:Enabled", "true"),
            ("ForwardedHeaders:KnownNetworks:0", "10.0.0.0/24")
        );

        await DrainAsync(client, "203.0.113.1", "192.0.2.50");

        var response = await client.PostShortenAsync(
            ShortenClientExtensions.UniqueUrl(),
            peer: "192.0.2.50", // where the request comes from
            forwardedFor: "203.0.113.2"
        );
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    // turning forwarding on without saying whom to trust is a config mistake: fail at startup
    // rather than guess
    [Fact]
    public void Enabled_without_any_trusted_proxy_stops_startup()
    {
        Assert.Throws<OptionsValidationException>(() =>
            ClientWith(("ForwardedHeaders:Enabled", "true"))
        );
    }

    private static async Task DrainAsync(
        HttpClient client,
        string forwardedFor,
        string peer = Proxy
    )
    {
        var first = await client.PostShortenAsync(
            ShortenClientExtensions.UniqueUrl(),
            peer: peer,
            forwardedFor: forwardedFor
        );
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await client.PostShortenAsync(
            ShortenClientExtensions.UniqueUrl(),
            peer: peer,
            forwardedFor: forwardedFor
        );
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        var third = await client.PostShortenAsync(
            ShortenClientExtensions.UniqueUrl(),
            peer: peer,
            forwardedFor: forwardedFor
        );
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
    }

    // a fresh app with the given settings (fresh rate-limit buckets too)
    private HttpClient ClientWith(params (string Key, string Value)[] settings) =>
        baseFactory
            .WithWebHostBuilder(b =>
            {
                foreach (var (key, value) in settings)
                {
                    b.UseSetting(key, value);
                }
            })
            .CreateClient();
}
