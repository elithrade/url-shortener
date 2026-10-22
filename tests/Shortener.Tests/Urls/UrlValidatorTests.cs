namespace Shortener.Tests;

public class UrlValidatorTests
{
    private const string OwnHost = "localhost";

    private static bool Validate(string? input, out Uri? uri, out string? error) =>
        UrlValidator.TryValidate(input, OwnHost, out uri, out error);

    // ---- rejected input ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void Rejects_null_empty_or_whitespace(string? input)
    {
        Assert.False(Validate(input, out var uri, out var error));
        Assert.Null(uri);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData("foo")]
    [InlineData("example.com")] // no scheme
    [InlineData("//example.com/a")] // scheme-relative
    [InlineData("/etc/passwd")] // parses as an absolute file URI on Linux/macOS
    [InlineData("http://")]
    [InlineData("https:///path")] // scheme but no host
    [InlineData("http://exa mple.com")] // space in host
    public void Rejects_unparseable_or_hostless_urls(string input)
    {
        Assert.False(Validate(input, out var uri, out var error));
        Assert.Null(uri);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("JAVASCRIPT:alert(1)")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com/file")]
    [InlineData("mailto:a@b.com")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("ws://example.com")]
    public void Rejects_non_http_schemes(string input)
    {
        Assert.False(Validate(input, out var uri, out var error));
        Assert.Null(uri);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData("https://google.com@evil.com")]
    [InlineData("https://user:pass@example.com")]
    public void Rejects_urls_with_user_info(string input)
    {
        Assert.False(Validate(input, out var uri, out var error));
        Assert.Null(uri);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    // ---- self-links (redirect loops and chains) ----

    [Theory]
    [InlineData("http://localhost/abc")]
    [InlineData("http://localhost:5154/abc")]
    [InlineData("https://localhost:7160/abc")]
    [InlineData("http://LOCALHOST/abc")] // host comparison is case-insensitive
    [InlineData("HTTP://LocalHost:5154/")]
    public void Rejects_links_to_own_host(string input)
    {
        Assert.False(Validate(input, out var uri, out var error));
        Assert.Null(uri);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData("http://notlocalhost.com/a")] // must be an exact host match, not Contains/EndsWith
    [InlineData("http://localhost.example.com/a")]
    [InlineData("http://example.com/localhost")]
    [InlineData("http://example.com/?next=http://localhost/")]
    public void Accepts_urls_that_merely_mention_own_host(string input)
    {
        Assert.True(Validate(input, out var uri, out var error));
        Assert.NotNull(uri);
        Assert.Null(error);
    }

    // ---- length limit ----

    private static string UrlOfLength(int length)
    {
        const string prefix = "https://example.com/";
        return prefix + new string('a', length - prefix.Length);
    }

    [Fact]
    public void Accepts_url_at_max_length()
    {
        var input = UrlOfLength(2048);

        Assert.Equal(2048, input.Length);
        Assert.True(Validate(input, out var uri, out var error));
        Assert.NotNull(uri);
        Assert.Null(error);
    }

    [Theory]
    [InlineData(2049)]
    [InlineData(3000)]
    [InlineData(1_000_000)]
    public void Rejects_url_over_max_length(int length)
    {
        Assert.False(Validate(UrlOfLength(length), out var uri, out var error));
        Assert.Null(uri);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    // ---- accepted input and canonical form ----

    [Theory]
    [InlineData("https://example.com", "https://example.com/")]
    [InlineData("HTTPS://Example.COM/Path?x=1", "https://example.com/Path?x=1")] // scheme/host lowercased, path/query case kept
    [InlineData("http://example.com:80/a", "http://example.com/a")] // default http port dropped
    [InlineData("https://example.com:443/a", "https://example.com/a")] // default https port dropped
    [InlineData("http://example.com:8080/a", "http://example.com:8080/a")] // non-default port kept
    [InlineData("https://example.com/a/B?Token=AbC#Frag", "https://example.com/a/B?Token=AbC#Frag")] // path/query/fragment case untouched
    [InlineData("http://sub.example.co.uk/a%20b", "http://sub.example.co.uk/a%20b")]
    public void Accepts_valid_urls_and_returns_canonical_uri(string input, string expectedAbsoluteUri)
    {
        Assert.True(Validate(input, out var uri, out var error));
        Assert.NotNull(uri);
        Assert.Null(error);
        Assert.Equal(expectedAbsoluteUri, uri.AbsoluteUri);
    }

    [Fact]
    public void Accepts_ip_literal_hosts_that_are_not_own_host()
    {
        Assert.True(Validate("http://93.184.216.34/a", out var uri, out _));
        Assert.NotNull(uri);
    }

    // ---- Try-pattern contract ----

    [Theory]
    [InlineData("https://example.com/a")]
    [InlineData("foo")]
    [InlineData(null)]
    public void Exactly_one_of_uri_or_error_is_set(string? input)
    {
        var ok = Validate(input, out var uri, out var error);

        Assert.Equal(ok, uri is not null);
        Assert.Equal(!ok, error is not null);
    }

    [Fact]
    public void Does_not_throw_on_hostile_input()
    {
        var inputs = new[]
        {
            new string('%', 5000),
            "http://[",
            "http://[::1",
            "http://\0example.com",
            "https://example.com/\u0000",
            "http://xn--",
        };

        foreach (var input in inputs)
        {
            var ex = Record.Exception(() => Validate(input, out _, out _));
            Assert.Null(ex);
        }
    }

    // ---- own host is a parameter, not a constant ----

    [Fact]
    public void Own_host_comes_from_the_parameter()
    {
        Assert.False(UrlValidator.TryValidate("http://sho.rt/abc", "sho.rt", out _, out _));
        Assert.True(UrlValidator.TryValidate("http://sho.rt/abc", "localhost", out var uri, out _));
        Assert.NotNull(uri);
    }
}
