using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Shortener.Tests;

public sealed class StrictRateLimitFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // refills one token per hour
        builder.UseSetting("RateLimit:Capacity", "2");
        builder.UseSetting("RateLimit:TokensPerPeriod", "1");
        builder.UseSetting("RateLimit:PeriodSeconds", "3600");

        builder.ConfigureServices(s => s.AddSingleton<IStartupFilter, RemoteIpStartupFilter>());
    }
}
