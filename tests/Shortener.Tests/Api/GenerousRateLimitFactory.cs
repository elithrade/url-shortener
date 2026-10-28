using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Shortener.Tests;

public sealed class GenerousRateLimitFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("RateLimit:Capacity", "100000");
        builder.UseSetting("RateLimit:TokensPerPeriod", "10");
        builder.UseSetting("RateLimit:PeriodSeconds", "1");
        builder.ConfigureServices(s => s.AddSingleton<IStartupFilter, RemoteIpStartupFilter>());
    }
}
