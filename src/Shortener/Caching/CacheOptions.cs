using System.ComponentModel.DataAnnotations;

namespace Shortener;

// bound from the Cache section; the cache is on only when ConnectionStrings:Redis is set
public sealed class CacheOptions
{
    public const string Section = "Cache";

    // longest time a link stays cached; the key's TTL is the smaller of this and the link's
    // remaining lifetime
    [Range(1, int.MaxValue)]
    public int TtlSeconds { get; init; }

    // how long a Redis call may take before it counts as an error (and a miss); the client's
    // default is 5 s, far too long to add to a redirect
    [Range(1, 5000)]
    public int TimeoutMilliseconds { get; init; }

    // prepended to every code, so one Redis can hold other data (and each test its own keys)
    [Required]
    public string KeyPrefix { get; init; } = "";
}
