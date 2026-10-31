using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Shortener;

public sealed class PostgresHealthCheck(IDbContextFactory<ShortenerDbContext> factory)
    : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken ct = default
    )
    {
        await using var dbContext = await factory.CreateDbContextAsync(ct);
        var canConnect = await dbContext.Database.CanConnectAsync(ct).WaitAsync(ct);

        return canConnect
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Unable to connect to the database");
    }
}
