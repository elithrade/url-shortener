using Microsoft.EntityFrameworkCore;

namespace Shortener;

public sealed class PostgresClickStore(IDbContextFactory<ShortenerDbContext> factory) : IClickStore
{
    private readonly IDbContextFactory<ShortenerDbContext> _factory = factory;

    public async ValueTask AddAsync(IReadOnlyCollection<DailyClicks> clicks, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (clicks.Count == 0)
        {
            return;
        }

        if (clicks.Any(c => c.Count <= 0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(clicks),
                "Count must be greater than zero"
            );
        }

        var codes = clicks.Select(c => c.Code).ToArray();
        var days = clicks.Select(c => c.Day).ToArray();
        var counts = clicks.Select(c => c.Count).ToArray();

        FormattableString query = $"""
            INSERT INTO click_stats ("Code", "Day", "Count")
            SELECT i.code, i.day, SUM(i.count)
            FROM unnest({codes}, {days}, {counts}) AS i(code, day, count)
            JOIN links l ON l."Code" = i.code
            GROUP BY i.code, i.day
            ON CONFLICT ("Code", "Day")
            DO UPDATE SET "Count" = click_stats."Count" + EXCLUDED."Count";
            """;
        try
        {
            await using var db = await _factory.CreateDbContextAsync(ct);
            await db.Database.ExecuteSqlAsync(query, ct);
        }
        catch (Exception ex) when (ex.IsUnavailable())
        {
            throw ex.Unavailable();
        }
    }

    public async ValueTask<long> GetAsync(
        string code,
        DateOnly start,
        DateOnly end,
        CancellationToken ct
    )
    {
        if (start > end)
        {
            throw new ArgumentException("start day must not be after end day", nameof(start));
        }

        try
        {
            await using var db = await _factory.CreateDbContextAsync(ct);

            return await db
                .ClickStats.Where(s => s.Code == code && s.Day >= start && s.Day <= end)
                .SumAsync(s => s.Count, ct);
        }
        catch (Exception ex) when (ex.IsUnavailable())
        {
            throw ex.Unavailable();
        }
    }
}
