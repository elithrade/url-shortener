using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Shortener.Tests;

public sealed class SpyClicksFactory : WebApplicationFactory<Program>
{
    public SpyClickStore Store { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Clicks:MaxBatchSize", "1");
        builder.ConfigureServices(s => s.AddSingleton<IClickStore>(Store));
    }
}
