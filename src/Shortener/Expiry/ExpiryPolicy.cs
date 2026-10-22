using System.Diagnostics.CodeAnalysis;

namespace Shortener;

public static class ExpiryPolicy
{
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromDays(30);
    private static readonly TimeSpan MinLifetime = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxLifetime = TimeSpan.FromDays(365);

    public static bool TryResolve(
        int? expiresInSeconds,
        DateTimeOffset now,
        [NotNullWhen(true)] out DateTimeOffset? expiresAt,
        [NotNullWhen(false)] out string? error
    )
    {
        error = null;
        expiresAt = now.Add(DefaultLifetime);

        if (expiresInSeconds is not null)
        {
            if (
                expiresInSeconds.Value > MaxLifetime.TotalSeconds
                || expiresInSeconds < MinLifetime.TotalSeconds
            )
            {
                expiresAt = null;
                error =
                    $"expiresInSeconds must be between {MinLifetime.TotalSeconds} and {MaxLifetime.TotalSeconds}";
                return false;
            }

            expiresAt = now.Add(TimeSpan.FromSeconds(expiresInSeconds.Value));

            return true;
        }

        return true;
    }
}
