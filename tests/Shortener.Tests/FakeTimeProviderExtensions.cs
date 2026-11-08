using Microsoft.Extensions.Time.Testing;

namespace Shortener.Tests;

// In .NET 10 BackgroundService.StartAsync runs ExecuteAsync through Task.Run, so a test's first
// Advance can happen before the service has created its timer, and that tick is lost. These
// helpers advance one step at a time until the service shows it has acted.
internal static class FakeTimeProviderExtensions
{
    // advances by step until signal returns true; signal should wait briefly for the effect
    public static async Task AdvanceUntilAsync(
        this FakeTimeProvider clock,
        TimeSpan step,
        Func<Task<bool>> signal,
        int attempts = 10
    )
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            clock.Advance(step);
            if (await signal())
            {
                return;
            }
        }
        throw new TimeoutException($"Nothing happened after {attempts} advances of {step}");
    }

    // advances by step until next returns a value, and returns it
    public static async Task<T> AdvanceUntilAsync<T>(
        this FakeTimeProvider clock,
        TimeSpan step,
        Func<Task<T?>> next,
        int attempts = 10
    )
        where T : class
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            clock.Advance(step);
            if (await next() is { } result)
            {
                return result;
            }
        }
        throw new TimeoutException($"Nothing happened after {attempts} advances of {step}");
    }
}
