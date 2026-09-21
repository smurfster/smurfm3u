using Microsoft.EntityFrameworkCore;
using Smurfm3u.Core.Entities;

namespace Smurfm3u.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<M3uSource> Sources => Set<M3uSource>();
    public DbSet<M3uItem> Items => Set<M3uItem>();
    public DbSet<DownloadItem> Downloads => Set<DownloadItem>();
    public DbSet<SearchHistoryEntry> Searches => Set<SearchHistoryEntry>();
    public DbSet<SpeedLimitWindow> SpeedLimits => Set<SpeedLimitWindow>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<AppSetting> Settings => Set<AppSetting>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // Trigram indexes make the "contains" searches Prowlarr sends usable at playlist scale.
        b.HasPostgresExtension("pg_trgm");

        b.Entity<M3uSource>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Location).HasMaxLength(2048).IsRequired();
            e.Property(x => x.QualityTag).HasMaxLength(50);
            e.Property(x => x.ResolutionTag).HasMaxLength(50);
            e.Property(x => x.ReleaseGroup).HasMaxLength(50);
            e.Property(x => x.RefreshCron).HasMaxLength(100);
            e.Property(x => x.LastRefreshError).HasMaxLength(2000);
            e.HasIndex(x => x.Name).IsUnique();
        });

        b.Entity<M3uItem>(e =>
        {
            e.Property(x => x.ItemKey).HasMaxLength(64).IsRequired();
            e.Property(x => x.RawTitle).HasMaxLength(1000).IsRequired();
            e.Property(x => x.StreamUrl).HasMaxLength(2048).IsRequired();
            e.Property(x => x.GroupTitle).HasMaxLength(300);
            e.Property(x => x.TvgId).HasMaxLength(200);
            e.Property(x => x.TvgName).HasMaxLength(300);
            e.Property(x => x.TvgLogo).HasMaxLength(2048);
            e.Property(x => x.Title).HasMaxLength(500);
            e.Property(x => x.SearchTitle).HasMaxLength(500);
            e.Property(x => x.EpisodeTitle).HasMaxLength(500);
            e.Property(x => x.Extension).HasMaxLength(10);

            e.HasOne(x => x.Source)
                .WithMany(x => x.Items)
                .HasForeignKey(x => x.SourceId)
                .OnDelete(DeleteBehavior.Cascade);

            // Identity of an entry within a source; refreshes upsert against this.
            e.HasIndex(x => new { x.SourceId, x.ItemKey }).IsUnique();

            e.HasIndex(x => new { x.Kind, x.IsActive });
            e.HasIndex(x => new { x.SearchTitle, x.Season, x.Episode });

            e.HasIndex(x => x.SearchTitle)
                .HasDatabaseName("ix_items_searchtitle_trgm")
                .HasMethod("gin")
                .HasOperators("gin_trgm_ops");
        });

        b.Entity<DownloadItem>(e =>
        {
            e.Property(x => x.NzoId).HasMaxLength(64).IsRequired();
            e.Property(x => x.Name).HasMaxLength(500).IsRequired();
            e.Property(x => x.Category).HasMaxLength(100);
            e.Property(x => x.StreamUrl).HasMaxLength(2048);
            e.Property(x => x.IncompletePath).HasMaxLength(1024);
            e.Property(x => x.CompletedPath).HasMaxLength(1024);
            e.Property(x => x.FailureMessage).HasMaxLength(2000);

            e.HasIndex(x => x.NzoId).IsUnique();
            e.HasIndex(x => new { x.Status, x.Priority, x.QueuedAt });
            e.HasIndex(x => x.CompletedAt);

            // Keep finished rows readable after their playlist entry or source is gone.
            e.HasOne(x => x.M3uItem).WithMany().HasForeignKey(x => x.M3uItemId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Source).WithMany().HasForeignKey(x => x.SourceId).OnDelete(DeleteBehavior.SetNull);

            e.Ignore(x => x.IsActive);
        });

        b.Entity<SearchHistoryEntry>(e =>
        {
            e.Property(x => x.Query).HasMaxLength(500);
            e.Property(x => x.ImdbId).HasMaxLength(20);
            e.Property(x => x.TvdbId).HasMaxLength(20);
            e.Property(x => x.TmdbId).HasMaxLength(20);
            e.Property(x => x.Categories).HasMaxLength(200);
            e.Property(x => x.ClientIp).HasMaxLength(64);
            e.Property(x => x.UserAgent).HasMaxLength(500);

            // A native array: one column, no join table, and no rows to cascade on delete.
            e.Property(x => x.ResultItemIds).HasColumnType("bigint[]");

            e.HasIndex(x => x.RequestedAt);
        });

        b.Entity<SpeedLimitWindow>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
        });

        b.Entity<AppUser>(e =>
        {
            e.Property(x => x.Username).HasMaxLength(100).IsRequired();
            e.Property(x => x.PasswordHash).HasMaxLength(500).IsRequired();
            e.HasIndex(x => x.Username).IsUnique();
        });

        b.Entity<AppSetting>(e =>
        {
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(100);
            e.Property(x => x.Value).HasMaxLength(4000);
        });
    }
}
