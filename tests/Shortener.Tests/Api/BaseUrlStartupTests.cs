using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Shortener.Tests;

// The rules are covered in ShortenerOptionsValidatorTests. These only prove the validator
// is wired up: a bad BaseUrl stops the app, a good one doesn't.
public class BaseUrlStartupTests
{
    private static WebApplicationFactory<Program> ProductionFactory(string? baseUrl) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            // Production does not load appsettings.Development.json, so BaseUrl is unset by default
            b.UseEnvironment(Environments.Production);
            if (baseUrl is not null)
            {
                b.UseSetting("Shortener:BaseUrl", baseUrl);
            }
        });

    [Fact]
    public void Missing_base_url_stops_startup()
    {
        using var factory = ProductionFactory(null);

        var ex = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
        Assert.Contains("Shortener:BaseUrl is required", ex.Message);
    }

    [Fact]
    public void Http_base_url_outside_development_stops_startup()
    {
        using var factory = ProductionFactory("http://example.com");

        var ex = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
        Assert.Contains("Shortener:BaseUrl must use https outside Development", ex.Message);
    }

    [Fact]
    public void Valid_base_url_starts()
    {
        using var factory = ProductionFactory("https://example.com");

        using var client = factory.CreateClient();
    }
}
