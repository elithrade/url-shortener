using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Shortener;

public sealed class WarmUpHealthCheck(WarmUpService warmup) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken ct = default
    )
    {
        var completed = warmup.Ready.IsCompletedSuccessfully;

        return completed
            ? Task.FromResult(HealthCheckResult.Healthy())
            : Task.FromResult(HealthCheckResult.Unhealthy("Warming up"));
    }
}
