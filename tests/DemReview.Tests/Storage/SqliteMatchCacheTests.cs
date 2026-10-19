using DemReview.Application.Parsing;
using DemReview.Domain;
using DemReview.Infrastructure.Platform;
using DemReview.Infrastructure.Storage;
using DemReview.Tests.Context;
using DemReview.Tests.Parsing;
using Microsoft.Data.Sqlite;

namespace DemReview.Tests.Storage;

public sealed class SqliteMatchCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(AppContext.BaseDirectory, "cache-tests", Guid.NewGuid().ToString("N"));
    private readonly string _db;

    public SqliteMatchCacheTests()
    {
        Directory.CreateDirectory(_dir);
        _db = Path.Combine(_dir, "тест кеш.db"); // пробел и кириллица в пути
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private SqliteMatchCache NewCache() =>
        new(InfrastructurePlatform(), new CollectingLogger<SqliteMatchCache>(), _db);

    private static Application.IPlatformService InfrastructurePlatform() =>
        DemReview.Infrastructure.InfrastructureServiceCollectionExtensions.CreatePlatformService();

    private static DemoFileKey Key(long length = 100, int step = 16) =>
        new(Path.Combine("C:", "демки", "match.dem"), length, new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc), step);

    private static MatchData Match() =>
        new SyntheticMatch().FiveVsFive().Rounds(3)
            .Route(1, 1, "TSpawn", "Ramp")
            .Kill(1, 10, 6, 1)
            .Grenade(1, 2, GrenadeType.Smoke)
            .Bomb(2, 30, BombEventType.Planted, BombSite.B)
            .Damage(3, 5, 1, 6)
            .Build();

    [Fact]
    public void Match_survives_restart_through_sqlite()
    {
        var original = Match();
        NewCache().Store(Key(), original);

        // Новый экземпляр — память пуста, данные только в SQLite.
        Assert.True(NewCache().TryGet(Key(), out var loaded));

        Assert.Equal(original.MapName, loaded.MapName);
        Assert.Equal(original.Rounds, loaded.Rounds);
        Assert.Equal(original.Kills.Single().VictimPosition, loaded.Kills.Single().VictimPosition);
        Assert.Equal(original.Players.Count, loaded.Players.Count);
        Assert.Equal(original.FindPlayer(6)!.SideByRound, loaded.FindPlayer(6)!.SideByRound);
        Assert.Equal(original.TrackOf(1).Samples, loaded.TrackOf(1).Samples);
        Assert.Equal(original.Grenades.Single().DetonatePosition, loaded.Grenades.Single().DetonatePosition);
        Assert.Equal(original.Bombs, loaded.Bombs);
        Assert.Equal(original.Damages, loaded.Damages);
    }

    [Fact]
    public void Changed_file_or_sample_step_is_a_miss()
    {
        NewCache().Store(Key(), Match());

        Assert.False(NewCache().TryGet(Key(length: 101), out _));
        Assert.False(NewCache().TryGet(Key(step: 8), out _));
        Assert.True(NewCache().TryGet(Key(), out _));
    }

    [Fact]
    public void Corrupted_database_is_recreated()
    {
        File.WriteAllText(_db, "это не база sqlite");

        var cache = NewCache();
        cache.Store(Key(), Match());

        Assert.True(NewCache().TryGet(Key(), out _));
    }

    [Fact]
    public void Compression_keeps_data_compact()
    {
        var match = Match();
        var bytes = SqliteMatchCache.Serialize(match);

        Assert.True(bytes.Length < 200_000, $"{bytes.Length} байт");
        Assert.Equal(match.Rounds, SqliteMatchCache.Deserialize(bytes).Rounds);
    }
}
