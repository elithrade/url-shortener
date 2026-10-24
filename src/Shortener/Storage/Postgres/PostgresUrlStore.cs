using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Shortener;

public sealed class PostgresUrlStore(
    IDbContextFactory<ShortenerDbContext> factory,
    TimeProvider clock
) : IUrlStore, IExpirySweeper
{
    private readonly IDbContextFactory<ShortenerDbContext> _factory = factory;
    private readonly TimeProvider _clock = clock;

    // EF wraps a transient failure in an InvalidOperationException, so the NpgsqlException is found in the inner chain.
    private static bool IsUnavailable(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is NpgsqlException { IsTransient: true })
            {
                return true;
            }
        }

        return false;
    }

    private static StorageUnavailableException Unavailable(Exception inner) =>
        new("Storage is unavailable", inner);

    public async ValueTask<string?> GetAsync(string code, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        try
        {
            await using var db = await _factory.CreateDbContextAsync(ct);

            return await db
                .Links.AsNoTracking()
                // code is case-sensitive
                .Where(r => r.Code == code)
                .Where(r => r.ExpiresAt == null || r.ExpiresAt > now)
                .Select(r => r.Url)
                .FirstOrDefaultAsync(ct);
        }
        catch (Exception ex) when (IsUnavailable(ex))
        {
            throw Unavailable(ex);
        }
    }

    public async ValueTask<int> SweepAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        try
        {
            await using var db = await _factory.CreateDbContextAsync(ct);

            return await db
                .Links.Where(r => r.ExpiresAt != null && r.ExpiresAt <= now)
                .ExecuteDeleteAsync(ct);
        }
        catch (Exception ex) when (IsUnavailable(ex))
        {
            throw Unavailable(ex);
        }
    }

    public async ValueTask<bool> TryAddAsync(string code, LinkEntry linkEntry, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        // Postgres cannot handle non-UTC time
        var expiresAt = linkEntry.ExpiresAt?.ToUniversalTime();

        // when ExpiresAt is null, it means never expire
        if (expiresAt <= now)
        {
            throw new ArgumentOutOfRangeException(
                nameof(linkEntry),
                "ExpiresAt must be in the future"
            );
        }

        await using var db = await _factory.CreateDbContextAsync(ct);
        try
        {
            await db.AddAsync(
                new LinkRecord()
                {
                    Code = code,
                    Url = linkEntry.Url,
                    ExpiresAt = expiresAt,
                },
                ct
            );

            await db.SaveChangesAsync(ct);

            return true;
        }
        catch (DbUpdateException ex)
            when (ex.InnerException
                    is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }
            )
        {
            return false;
        }
        catch (Exception ex) when (IsUnavailable(ex))
        {
            throw Unavailable(ex);
        }
    }
}
