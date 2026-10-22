using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Shortener;

public sealed class PostgresUrlStore(
    IDbContextFactory<ShortenerDbContext> factory,
    TimeProvider clock
) : IUrlStore
{
    private readonly IDbContextFactory<ShortenerDbContext> _factory = factory;
    private readonly TimeProvider _clock = clock;

    public async ValueTask<string?> GetAsync(string code, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        await using var db = await _factory.CreateDbContextAsync(ct);

        return await db
            .Links.AsNoTracking()
            // code is case-sensitive
            .Where(r => r.Code == code)
            .Where(r => r.ExpiresAt == null || r.ExpiresAt > now)
            .Select(r => r.Url)
            .FirstOrDefaultAsync(ct);
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
    }
}
