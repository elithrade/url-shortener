using Microsoft.EntityFrameworkCore;

namespace Shortener;

public sealed class ShortenerDbContext(DbContextOptions<ShortenerDbContext> options)
    : DbContext(options)
{
    public DbSet<LinkRecord> Links => Set<LinkRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<LinkRecord>(e =>
        {
            e.ToTable("links");
            e.HasKey(l => l.Code);
            e.Property(l => l.Code).HasMaxLength(16);
            e.Property(l => l.Url).HasMaxLength(2048);
            e.HasIndex(l => l.ExpiresAt);
        });
    }
}
