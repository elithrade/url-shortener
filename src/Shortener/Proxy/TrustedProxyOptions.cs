using System.ComponentModel.DataAnnotations;

namespace Shortener;

// bound from the ForwardedHeaders section: whose X-Forwarded-For to believe.
// Off by default; a deployment behind a proxy turns it on and names the proxy.
public sealed class TrustedProxyOptions
{
    public const string Section = "ForwardedHeaders";

    public bool Enabled { get; init; }

    // only when the app can be reached through the proxy alone (e.g. Container Apps ingress)
    public bool TrustAnyProxy { get; init; }

    // CIDR ranges of the proxies, e.g. "10.0.0.0/24"
    public string[] KnownNetworks { get; init; } = [];

    // how many entries to read from the right of X-Forwarded-For: one per proxy we run
    [Range(1, int.MaxValue)]
    public int ForwardLimit { get; init; } = 1;
}
