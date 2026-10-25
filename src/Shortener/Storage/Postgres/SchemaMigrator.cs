using Microsoft.EntityFrameworkCore;

namespace Shortener;

public static class SchemaMigrator
{
    public static async Task<int> ExecuteAsync(
        IDbContextFactory<ShortenerDbContext> factory,
        ILogger logger
    )
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            await db.Database.MigrateAsync();

            return 0;
        }
        catch (Exception ex)
        {
            MigrationLog.MigrationFailed(logger, ex);

            return 1;
        }
    }
}

internal static partial class MigrationLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Database migration failed")]
    public static partial void MigrationFailed(ILogger logger, Exception ex);
}
