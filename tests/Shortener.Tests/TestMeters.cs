using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;

namespace Shortener.Tests;

internal static class TestMeters
{
    // a meter factory of its own, so a MetricCollector scoped to it sees only one test's counts
    public static IMeterFactory CreateFactory() =>
        new ServiceCollection()
            .AddMetrics()
            .BuildServiceProvider()
            .GetRequiredService<IMeterFactory>();
}
