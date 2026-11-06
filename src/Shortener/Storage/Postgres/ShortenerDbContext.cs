using Microsoft.EntityFrameworkCore;

namespace Shortener;

public sealed class ShortenerDbContext(DbContextOptions<ShortenerDbContext> options)
    : DbContext(options)
{
    public DbSet<LinkRecord> Links => Set<LinkRecord>();
    public DbSet<ClickStatRecord> ClickStats => Set<ClickStatRecord>();

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
        modelBuilder.Entity<ClickStatRecord>(e =>
        {
            e.ToTable("click_stats");
            // code first: a read is "one code, a range of days", which is one contiguous slice of this index;
            // the key is also the unique constraint that the upsert's ON CONFLICT needs
            e.HasKey(c => new { c.Code, c.Day });
            e.Property(c => c.Code).HasMaxLength(16);
            // a swept link takes its stats with it, so a reused code starts from zero
            e.HasOne<LinkRecord>()
                .WithMany()
                .HasForeignKey(c => c.Code)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
