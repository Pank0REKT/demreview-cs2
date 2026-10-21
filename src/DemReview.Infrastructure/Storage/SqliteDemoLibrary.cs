using System.Text.Json;
using DemReview.Application;
using DemReview.Application.Library;
using DemReview.Application.Parsing;
using DemReview.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DemReview.Infrastructure.Storage;

/// <summary>
/// Библиотека разобранных демок в отдельной базе SQLite: в отличие от кеша не ограничена 50 демками
/// и не очищается вместе с ним. Матч хранится в том же формате, что и в кеше (JSON + Brotli).
/// Повреждённая база не удаляется, а откладывается в сторону (.broken) — библиотеку можно собрать заново.
/// </summary>
public sealed class SqliteDemoLibrary : IDemoLibrary
{
    /// <summary>Версия схемы БД (PRAGMA user_version). При изменении — миграция, а не пересоздание: здесь данные пользователя.</summary>
    private const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions Json = new();

    private readonly IPlatformService _platform;
    private readonly ILogger<SqliteDemoLibrary> _logger;
    private readonly DbContextOptions<LibraryDbContext> _options;
    private readonly Lock _lock = new();
    private bool _initialized;

    public SqliteDemoLibrary(IPlatformService platform, ILogger<SqliteDemoLibrary> logger)
        : this(platform, logger, Path.Combine(platform.AppDataDirectory, "library.db"))
    {
    }

    public SqliteDemoLibrary(IPlatformService platform, ILogger<SqliteDemoLibrary> logger, string databasePath)
    {
        _platform = platform;
        _logger = logger;
        DatabasePath = databasePath;
        var connection = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString();
        _options = new DbContextOptionsBuilder<LibraryDbContext>().UseSqlite(connection).Options;
    }

    public string DatabasePath { get; }

    public IReadOnlyList<LibraryEntry> List()
    {
        try
        {
            lock (_lock)
            {
                using var db = Open();
                return db.Matches.AsNoTracking()
                    .Select(m => new
                    {
                        m.Id, m.FullPath, m.MapName, m.TeamTName, m.TeamTScore, m.TeamCTName, m.TeamCTScore, m.RoundCount,
                        m.LastWriteTimeUtc, m.AddedUtc, m.PlayersJson, m.FormatVersion,
                    })
                    .AsEnumerable()
                    .Select(m => new LibraryEntry(m.Id, m.FullPath, m.MapName, new MatchScore(m.TeamTName, m.TeamTScore, m.TeamCTName, m.TeamCTScore),
                        m.RoundCount, m.LastWriteTimeUtc, m.AddedUtc, ReadPlayers(m.PlayersJson), m.FormatVersion == SqliteMatchCache.FormatVersion))
                    .OrderByDescending(e => e.FileTimeUtc)
                    .ToList();
            }
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            _logger.LogWarning(ex, "Не удалось прочитать библиотеку {Path}", DatabasePath);
            return [];
        }
    }

    public bool Contains(DemoFileKey key)
    {
        try
        {
            lock (_lock)
            {
                using var db = Open();
                var pathKey = _platform.NormalizePathKey(key.FullPath);
                return db.Matches.Any(m => m.PathKey == pathKey && m.Length == key.Length && m.LastWriteTimeUtc == key.LastWriteTimeUtc &&
                                           m.FormatVersion == SqliteMatchCache.FormatVersion);
            }
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            _logger.LogWarning(ex, "Не удалось прочитать библиотеку {Path}", DatabasePath);
            return false;
        }
    }

    public bool TryGet(DemoFileKey key, out MatchData match)
    {
        match = null!;
        try
        {
            lock (_lock)
            {
                using var db = Open();
                var pathKey = _platform.NormalizePathKey(key.FullPath);
                var entity = db.Matches.AsNoTracking().SingleOrDefault(m => m.PathKey == pathKey);
                if (entity is null || entity.Length != key.Length || entity.LastWriteTimeUtc != key.LastWriteTimeUtc ||
                    entity.SampleTickStep != key.SampleTickStep || entity.FormatVersion != SqliteMatchCache.FormatVersion)
                    return false;
                match = SqliteMatchCache.Deserialize(entity.Data) with { SourcePath = key.FullPath };
            }

            _logger.LogInformation("Демка загружена из библиотеки: {Path}", key.FullPath);
            return true;
        }
        catch (Exception ex) when (ex is SqliteException or JsonException or InvalidDataException or IOException)
        {
            _logger.LogWarning(ex, "Не удалось прочитать демку {Path} из библиотеки", key.FullPath);
            return false;
        }
    }

    public void Store(DemoFileKey key, MatchData match)
    {
        try
        {
            var data = SqliteMatchCache.Serialize(match);
            var players = JsonSerializer.Serialize(match.Players.Select(LibraryPlayer.From).ToList(), Json);
            lock (_lock)
            {
                using var db = Open();
                var pathKey = _platform.NormalizePathKey(key.FullPath);
                var entity = db.Matches.SingleOrDefault(m => m.PathKey == pathKey);
                if (entity is null)
                {
                    entity = new LibraryMatchEntity { PathKey = pathKey, FullPath = key.FullPath, MapName = match.MapName, PlayersJson = "", Data = [] };
                    db.Matches.Add(entity);
                }

                entity.FullPath = key.FullPath;
                entity.Length = key.Length;
                entity.LastWriteTimeUtc = key.LastWriteTimeUtc;
                entity.SampleTickStep = key.SampleTickStep;
                entity.FormatVersion = SqliteMatchCache.FormatVersion;
                entity.MapName = match.MapName;
                entity.TeamTName = match.FinalScore.TeamTName;
                entity.TeamTScore = match.FinalScore.TeamTScore;
                entity.TeamCTName = match.FinalScore.TeamCTName;
                entity.TeamCTScore = match.FinalScore.TeamCTScore;
                entity.RoundCount = match.Rounds.Count;
                entity.AddedUtc = DateTime.UtcNow;
                entity.PlayersJson = players;
                entity.Data = data;
                db.SaveChanges();
            }

            _logger.LogInformation("Демка сохранена в библиотеку: {Path}", key.FullPath);
        }
        catch (Exception ex) when (ex is SqliteException or DbUpdateException or IOException)
        {
            _logger.LogWarning(ex, "Не удалось сохранить демку в библиотеку: {Path}", key.FullPath);
        }
    }

    public MatchData? Load(int id)
    {
        try
        {
            lock (_lock)
            {
                using var db = Open();
                var entity = db.Matches.AsNoTracking().SingleOrDefault(m => m.Id == id);
                if (entity is null || entity.FormatVersion != SqliteMatchCache.FormatVersion)
                    return null;
                return SqliteMatchCache.Deserialize(entity.Data) with { SourcePath = entity.FullPath };
            }
        }
        catch (Exception ex) when (ex is SqliteException or JsonException or InvalidDataException or IOException)
        {
            _logger.LogWarning(ex, "Не удалось прочитать демку {Id} из библиотеки", id);
            return null;
        }
    }

    public void Remove(int id)
    {
        lock (_lock)
        {
            using var db = Open();
            db.Matches.Where(m => m.Id == id).ExecuteDelete();
        }
    }

    public MatchCacheInfo GetInfo()
    {
        try
        {
            lock (_lock)
            {
                using var db = Open();
                var entries = db.Matches.Count();
                var size = File.Exists(DatabasePath) ? new FileInfo(DatabasePath).Length : 0;
                return new MatchCacheInfo(entries, size, DatabasePath);
            }
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            _logger.LogWarning(ex, "Не удалось прочитать сведения о библиотеке {Path}", DatabasePath);
            return new MatchCacheInfo(0, 0, DatabasePath);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            using (var db = Open())
                db.Matches.ExecuteDelete();
            // Освободить место на диске сразу.
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "VACUUM;";
            command.ExecuteNonQuery();
        }
        _logger.LogInformation("Библиотека очищена: {Path}", DatabasePath);
    }

    private LibraryDbContext Open()
    {
        EnsureDatabase();
        return new LibraryDbContext(_options);
    }

    /// <summary>Создаёт БД при первом обращении; повреждённую — откладывает в сторону и создаёт новую.</summary>
    private void EnsureDatabase()
    {
        if (_initialized)
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        try
        {
            CreateSchema();
        }
        catch (SqliteException ex)
        {
            _logger.LogWarning(ex, "База библиотеки {Path} повреждена, создаётся новая", DatabasePath);
            SqliteConnection.ClearAllPools();
            File.Move(DatabasePath, DatabasePath + ".broken", overwrite: true);
            CreateSchema();
        }

        _initialized = true;
    }

    private void CreateSchema()
    {
        using var db = new LibraryDbContext(_options);
        if (db.Database.EnsureCreated())
            db.Database.ExecuteSqlRaw($"PRAGMA user_version = {SchemaVersion};");
        else
            _ = db.Matches.Count(); // проверка, что база читается
    }

    private static IReadOnlyList<LibraryPlayer> ReadPlayers(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<LibraryPlayer>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

/// <summary>Демка в библиотеке: метаданные и игроки для поиска без распаковки, сжатый JSON модели матча.</summary>
public sealed class LibraryMatchEntity
{
    public int Id { get; set; }

    /// <summary>Нормализованный путь (на Windows — в верхнем регистре).</summary>
    public required string PathKey { get; set; }

    public required string FullPath { get; set; }
    public long Length { get; set; }
    public DateTime LastWriteTimeUtc { get; set; }
    public int SampleTickStep { get; set; }
    public int FormatVersion { get; set; }
    public required string MapName { get; set; }
    public string TeamTName { get; set; } = "";
    public int TeamTScore { get; set; }
    public string TeamCTName { get; set; } = "";
    public int TeamCTScore { get; set; }
    public int RoundCount { get; set; }
    public DateTime AddedUtc { get; set; }

    /// <summary>Игроки матча (<see cref="LibraryPlayer"/>) в JSON.</summary>
    public required string PlayersJson { get; set; }

    /// <summary>MatchData в JSON, сжатый Brotli.</summary>
    public required byte[] Data { get; set; }
}

public sealed class LibraryDbContext(DbContextOptions<LibraryDbContext> options) : DbContext(options)
{
    public DbSet<LibraryMatchEntity> Matches => Set<LibraryMatchEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var match = modelBuilder.Entity<LibraryMatchEntity>();
        match.ToTable("library_matches");
        match.HasIndex(m => m.PathKey).IsUnique();
        match.Property(m => m.MapName).HasMaxLength(64);
    }
}
