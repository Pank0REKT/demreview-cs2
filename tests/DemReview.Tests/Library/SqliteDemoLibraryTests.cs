using DemReview.Application.Parsing;
using DemReview.Domain;
using DemReview.Infrastructure.Storage;
using DemReview.Tests.Context;
using DemReview.Tests.Parsing;
using Microsoft.Data.Sqlite;

namespace DemReview.Tests.Library;

public sealed class SqliteDemoLibraryTests : IDisposable
{
    private readonly string _dir = Path.Combine(AppContext.BaseDirectory, "library-tests", Guid.NewGuid().ToString("N"));
    private readonly string _db;

    public SqliteDemoLibraryTests()
    {
        Directory.CreateDirectory(_dir);
        _db = Path.Combine(_dir, "библиотека демок.db"); // пробел и кириллица в пути
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private SqliteDemoLibrary NewLibrary() =>
        new(DemReview.Infrastructure.InfrastructureServiceCollectionExtensions.CreatePlatformService(), new CollectingLogger<SqliteDemoLibrary>(), _db);

    private static DemoFileKey Key(string name = "match.dem", long length = 100) =>
        new(Path.Combine("C:", "демки", name), length, new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc));

    private static MatchData Match() =>
        new SyntheticMatch().FiveVsFive().Rounds(3).Route(1, 1, "TSpawn", "Ramp").Kill(1, 10, 6, 1).Build() with
        {
            FinalScore = new MatchScore("Alpha", 13, "Bravo", 9),
        };

    [Fact]
    public void Stored_demo_is_listed_with_players_and_survives_restart()
    {
        NewLibrary().Store(Key(), Match());

        var library = NewLibrary();
        var entry = Assert.Single(library.List());
        Assert.Equal("match.dem", entry.FileName);
        Assert.Equal("de_mirage", entry.MapName);
        Assert.Equal(new MatchScore("Alpha", 13, "Bravo", 9), entry.Score);
        Assert.Equal(3, entry.RoundCount);
        Assert.True(entry.IsCurrent);
        Assert.Equal(10, entry.Players.Count);
        Assert.Equal("p6", entry.FindPlayer(6)!.Name);

        var loaded = library.Load(entry.Id)!;
        Assert.Equal(Match().Rounds, loaded.Rounds);
        Assert.Equal(Key().FullPath, loaded.SourcePath);
    }

    [Fact]
    public void Contains_only_the_same_version_of_the_file()
    {
        var library = NewLibrary();
        library.Store(Key(), Match());

        Assert.True(library.Contains(Key()));
        Assert.False(library.Contains(Key(length: 101)));
        Assert.True(library.TryGet(Key(), out _));
        Assert.False(library.TryGet(Key() with { SampleTickStep = 8 }, out _));

        // Изменённый файл заменяет запись, а не добавляет вторую.
        library.Store(Key(length: 101), Match());
        Assert.Single(library.List());
        Assert.True(library.Contains(Key(length: 101)));
    }

    [Fact]
    public void Remove_and_clear()
    {
        var library = NewLibrary();
        library.Store(Key("a.dem"), Match());
        library.Store(Key("b.dem"), Match());
        Assert.Equal(2, library.GetInfo().Entries);

        library.Remove(library.List().First(e => e.FileName == "a.dem").Id);
        Assert.Equal("b.dem", Assert.Single(library.List()).FileName);

        library.Clear();
        Assert.Empty(library.List());
        Assert.Equal(0, NewLibrary().GetInfo().Entries);
    }

    [Fact]
    public void Corrupted_database_is_set_aside_not_deleted()
    {
        File.WriteAllText(_db, "это не база sqlite");

        var library = NewLibrary();
        library.Store(Key(), Match());

        Assert.Single(NewLibrary().List());
        Assert.True(File.Exists(_db + ".broken"));
    }
}
