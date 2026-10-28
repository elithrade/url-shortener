using System.Net;

namespace Shortener.Tests;

public class RateLimitKeyTests
{
    [Fact]
    public void Null_ipAddress_returns_fallback_key()
    {
        var key = RateLimitKey.For(null);
        Assert.Equal(RateLimitKey.Unknown, key);
    }

    [Fact]
    public void An_v4_ipAddress_is_its_own_key()
    {
        var ip = IPAddress.Parse("1.2.3.4");
        var key = RateLimitKey.For(ip);

        Assert.Equal("1.2.3.4", key);
    }

    [Theory]
    [InlineData("1.2.3.4", "::ffff:1.2.3.4")]
    [InlineData("fe80::1", "fe80::1%3")]
    [InlineData("2001:db8:85a3:1::1", "2001:db8:85a3:1:ffff:ffff:ffff:ffff")]
    public void Equivalent_addresses_share_a_key(string a, string b)
    {
        Assert.Equal(RateLimitKey.For(IPAddress.Parse(a)), RateLimitKey.For(IPAddress.Parse(b)));
    }

    [Theory]
    [InlineData("1.2.3.4", "1.2.3.5")]
    [InlineData("2001:db8:85a3:1::1", "2001:db8:85a3:2::1")]
    [InlineData("2001:db8::1", "2001:db9::1")]
    public void Different_addresses_give_different_keys(string a, string b)
    {
        Assert.NotEqual(RateLimitKey.For(IPAddress.Parse(a)), RateLimitKey.For(IPAddress.Parse(b)));
    }
}
