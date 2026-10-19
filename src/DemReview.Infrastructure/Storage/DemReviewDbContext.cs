using Microsoft.EntityFrameworkCore;

namespace DemReview.Infrastructure.Storage;

/// <summary>Разобранная демка в SQLite: метаданные для поиска и сжатый JSON модели матча.</summary>
public sealed class CachedMatchEntity
{
    public int Id { get; set; }

    /// <summary>Нормализованный путь (на Windows — в верхнем регистре).</summary>
    public required string PathKey { get; set; }

    public required string FullPath { get; set; }
    public long Length { get; set; }
    public DateTime LastWriteTimeUtc { get; set; }
    public int SampleTickStep { get; set; }

    /// <summary>Версия формата модели: при изменении MatchData старые записи игнорируются.</summary>
    public int FormatVersion { get; set; }

    public required string MapName { get; set; }
    public int RoundCount { get; set; }
    public int PlayerCount { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime LastUsedUtc { get; set; }

    /// <summary>MatchData в JSON, сжатый Brotli.</summary>
    public required byte[] Data { get; set; }
}

public sealed class DemReviewDbContext(DbContextOptions<DemReviewDbContext> options) : DbContext(options)
{
    public DbSet<CachedMatchEntity> Matches => Set<CachedMatchEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var match = modelBuilder.Entity<CachedMatchEntity>();
        match.ToTable("matches");
        match.HasIndex(m => new { m.PathKey, m.SampleTickStep }).IsUnique();
        match.Property(m => m.PathKey).IsRequired();
        match.Property(m => m.MapName).HasMaxLength(64);
    }
}
