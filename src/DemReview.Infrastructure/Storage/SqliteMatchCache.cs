using System.IO.Compression;
using System.Text.Json;
using DemReview.Application;
using DemReview.Application.Parsing;
using DemReview.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DemReview.Infrastructure.Storage;

/// <summary>
/// Кеш разобранных демок: в памяти на время сессии и в SQLite (EF Core) между запусками.
/// База лежит в каталоге данных пользователя; при несовместимой схеме или повреждении пересоздаётся —
/// это кеш, демку всегда можно разобрать заново.
/// </summary>
public sealed class SqliteMatchCache : IMatchCache
{
    /// <summary>Версия формата сериализованного MatchData. Увеличивать при изменении доменной модели или парсера.</summary>
    public const int FormatVersion = 1;

    /// <summary>Версия схемы БД (PRAGMA user_version).</summary>
    private const int SchemaVersion = 1;

    /// <summary>Сколько последних демок хранить.</summary>
    private const int MaxEntries = 50;

    private static readonly JsonSerializerOptions Json = new() { IncludeFields = true };

    private readonly InMemoryMatchCache _memory;
    private readonly IPlatformService _platform;
    private readonly ILogger<SqliteMatchCache> _logger;
    private readonly DbContextOptions<DemReviewDbContext> _options;
    private readonly Lock _lock = new();
    private bool _initialized;

    public SqliteMatchCache(IPlatformService platform, ILogger<SqliteMatchCache> logger)
        : this(platform, logger, Path.Combine(platform.AppDataDirectory, "demreview.db"))
    {
    }

    public SqliteMatchCache(IPlatformService platform, ILogger<SqliteMatchCache> logger, string databasePath)
    {
        _platform = platform;
        _logger = logger;
        _memory = new InMemoryMatchCache(platform);
        DatabasePath = databasePath;
        var connection = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString();
        _options = new DbContextOptionsBuilder<DemReviewDbContext>().UseSqlite(connection).Options;
    }

    public string DatabasePath { get; }

    public bool TryGet(DemoFileKey key, out MatchData match)
    {
        if (_memory.TryGet(key, out match))
            return true;

        try
        {
            lock (_lock)
            {
                EnsureDatabase();
                using var db = new DemReviewDbContext(_options);
                var pathKey = _platform.NormalizePathKey(key.FullPath);
                var entity = db.Matches.SingleOrDefault(m => m.PathKey == pathKey && m.SampleTickStep == key.SampleTickStep);
                if (entity is null || entity.Length != key.Length || entity.LastWriteTimeUtc != key.LastWriteTimeUtc ||
                    entity.FormatVersion != FormatVersion)
                {
                    match = null!;
                    return false;
                }

                match = Deserialize(entity.Data) with { SourcePath = key.FullPath };
                entity.LastUsedUtc = DateTime.UtcNow;
                db.SaveChanges();
            }

            _memory.Store(key, match);
            _logger.LogInformation("Демка загружена из SQLite: {Path}", key.FullPath);
            return true;
        }
        catch (Exception ex) when (ex is SqliteException or DbUpdateException or JsonException or InvalidDataException or IOException)
        {
            _logger.LogWarning(ex, "Не удалось прочитать кеш SQLite для {Path}", key.FullPath);
            match = null!;
            return false;
        }
    }

    public void Store(DemoFileKey key, MatchData match)
    {
        _memory.Store(key, match);
        try
        {
            lock (_lock)
            {
                EnsureDatabase();
                using var db = new DemReviewDbContext(_options);
                var pathKey = _platform.NormalizePathKey(key.FullPath);
                var entity = db.Matches.SingleOrDefault(m => m.PathKey == pathKey && m.SampleTickStep == key.SampleTickStep);
                var now = DateTime.UtcNow;
                if (entity is null)
                {
                    entity = new CachedMatchEntity { PathKey = pathKey, FullPath = key.FullPath, MapName = match.MapName, Data = [] };
                    db.Matches.Add(entity);
                    entity.CreatedUtc = now;
                }

                entity.FullPath = key.FullPath;
                entity.Length = key.Length;
                entity.LastWriteTimeUtc = key.LastWriteTimeUtc;
                entity.SampleTickStep = key.SampleTickStep;
                entity.FormatVersion = FormatVersion;
                entity.MapName = match.MapName;
                entity.RoundCount = match.Rounds.Count;
                entity.PlayerCount = match.Players.Count;
                entity.LastUsedUtc = now;
                entity.Data = Serialize(match);
                db.SaveChanges();

                // Храним только последние демки.
                var stale = db.Matches.OrderByDescending(m => m.LastUsedUtc).Skip(MaxEntries).ToList();
                if (stale.Count > 0)
                {
                    db.Matches.RemoveRange(stale);
                    db.SaveChanges();
                }
            }
        }
        catch (Exception ex) when (ex is SqliteException or DbUpdateException or IOException)
        {
            _logger.LogWarning(ex, "Не удалось сохранить демку в SQLite: {Path}", key.FullPath);
        }
    }

    /// <summary>Создаёт БД при первом обращении; несовместимую или повреждённую — пересоздаёт.</summary>
    private void EnsureDatabase()
    {
        if (_initialized)
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        try
        {
            if (File.Exists(DatabasePath) && ReadUserVersion() != SchemaVersion)
                Recreate("другая версия схемы");
            else
                CreateSchema();
        }
        catch (SqliteException ex)
        {
            _logger.LogWarning(ex, "База {Path} повреждена", DatabasePath);
            Recreate("повреждена");
        }

        _initialized = true;
    }

    private void CreateSchema()
    {
        using var db = new DemReviewDbContext(_options);
        if (db.Database.EnsureCreated())
            db.Database.ExecuteSqlRaw($"PRAGMA user_version = {SchemaVersion};");
    }

    private void Recreate(string reason)
    {
        _logger.LogInformation("Кеш SQLite пересоздаётся ({Reason}): {Path}", reason, DatabasePath);
        SqliteConnection.ClearAllPools();
        File.Delete(DatabasePath);
        CreateSchema();
    }

    private int ReadUserVersion()
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    internal static byte[] Serialize(MatchData match)
    {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.Fastest))
            JsonSerializer.Serialize(brotli, match, Json);
        return output.ToArray();
    }

    internal static MatchData Deserialize(byte[] data)
    {
        using var input = new BrotliStream(new MemoryStream(data), CompressionMode.Decompress);
        return JsonSerializer.Deserialize<MatchData>(input, Json) ?? throw new InvalidDataException("Пустые данные матча");
    }
}
