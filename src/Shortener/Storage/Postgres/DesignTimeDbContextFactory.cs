using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Shortener;

// Used only by `dotnet ef` to generate migrations. Generating one never connects to a database,
// so the connection string is a placeholder and no secret belongs here.
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ShortenerDbContext>
{
    public ShortenerDbContext CreateDbContext(string[] args) =>
        new(
            new DbContextOptionsBuilder<ShortenerDbContext>()
                .UseNpgsql("Host=localhost;Database=design_time")
                .Options
        );
}
