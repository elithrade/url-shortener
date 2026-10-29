using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Options;

namespace Shortener.Tests;

public class ShortenerOptionsValidatorTests
{
    [Theory]
    [InlineData("https://example.com", "Production", true)] // absolute
    [InlineData("http://example.com", "Staging", false)] // non prod non dev env
    [InlineData("https://example.com/", "Production", true)] // absolute
    [InlineData("example.com", "Production", false)] // non-absolute
    [InlineData("https://example.com/path/a", "Production", false)] // with path
    [InlineData("ftp://example.com", "Production", false)] // wrong scheme
    [InlineData("https://example.com?x=1", "Production", false)] // with query string
    [InlineData("https://user:pass@example.com", "Production", false)] // with user info
    [InlineData("    ", "Production", false)]
    [InlineData("", "Production", false)]
    [InlineData(null, "Production", false)]
    [InlineData("http://localhost:5154", "Production", false)]
    [InlineData("http://localhost:5154", "Development", true)]
    [InlineData("localhost:5154", "Development", false)]
    [InlineData("https://example.com#x", "Production", false)] // fragment not allowed
    public void Validates_base_url(string? url, string environment, bool expected)
    {
        // absolute, https or http in dev, no path, query, user info
        var env = new HostingEnvironment { EnvironmentName = environment };
        var validator = new ShortenerOptionsValidator(env);
        var options = new ShortenerOptions { BaseUrl = url };

        var result = validator.Validate(Options.DefaultName, options);

        Assert.Equal(expected, result.Succeeded);
    }
}
