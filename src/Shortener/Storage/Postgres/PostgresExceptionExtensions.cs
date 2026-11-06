using Npgsql;

namespace Shortener
{
    internal static class PostgresExceptionExtensions
    {
        // EF wraps a transient failure in an InvalidOperationException, so the NpgsqlException is found in the inner chain.
        public static bool IsUnavailable(this Exception ex)
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

        public static StorageUnavailableException Unavailable(this Exception inner) =>
            new("Storage is unavailable", inner);
    }
}
