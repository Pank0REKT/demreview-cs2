using System.Numerics;
using DemReview.Application.Lineups;
using DemReview.Domain;
using DemReview.Infrastructure.Storage;
using DemReview.Tests.Parsing;

namespace DemReview.Tests.Lineups;

public sealed class JsonLineupLibraryTests : IDisposable
{
    private readonly string _dir = Path.Combine(AppContext.BaseDirectory, "lineup-tests", Guid.NewGuid().ToString("N"));
    private readonly string _file;

    public JsonLineupLibraryTests()
    {
        _file = Path.Combine(_dir, "мои раскидки", "lineups.json"); // пробел и кириллица в пути, папки ещё нет
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private JsonLineupLibrary NewLibrary() => new(_file, new CollectingLogger<JsonLineupLibrary>());

    private static Lineup Lineup(float x = 100, GrenadeType type = GrenadeType.Smoke, string map = "de_mirage", DateTime? saved = null) =>
        new(0, map, type, Side.T, "TSpawn → Window", new Vector3(x, -200.5f, -167.96875f), -10, 90, MovementFlags.Airborne, 2.5f,
            new Vector3(1, 2, 3), "TSpawn", "Window", "alice", 76561198000000001, "match.dem", 4, 12345,
            saved ?? new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void Added_lineup_survives_restart()
    {
        var (saved, added) = NewLibrary().Add(Lineup());

        Assert.True(added);
        Assert.Equal(1, saved.Id);
        var loaded = Assert.Single(NewLibrary().List());
        Assert.Equal(saved, loaded);
    }

    [Fact]
    public void Same_throw_is_not_added_twice()
    {
        var library = NewLibrary();
        var (first, _) = library.Add(Lineup());

        var (second, added) = library.Add(Lineup() with { Title = "другое название" });

        Assert.False(added);
        Assert.Equal(first.Id, second.Id);
        Assert.Single(library.List());
    }

    [Fact]
    public void Lineups_are_listed_newest_first_and_ids_are_not_reused()
    {
        var library = NewLibrary();
        var (a, _) = library.Add(Lineup(x: 1, saved: new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)));
        var (b, _) = library.Add(Lineup(x: 2, saved: new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc)));
        library.Remove(b.Id);
        var (c, _) = library.Add(Lineup(x: 3, saved: new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc)));

        Assert.Equal([c.Id, a.Id], NewLibrary().List().Select(l => l.Id));
        Assert.Equal(2, c.Id);
    }

    [Fact]
    public void Rename_and_remove_are_saved()
    {
        var library = NewLibrary();
        var (a, _) = library.Add(Lineup(x: 1));
        var (b, _) = library.Add(Lineup(x: 2));

        library.Rename(a.Id, "Окно с T-спавна");
        library.Remove(b.Id);

        var loaded = Assert.Single(NewLibrary().List());
        Assert.Equal("Окно с T-спавна", loaded.Title);
    }

    [Fact]
    public void Unreadable_file_is_moved_aside_and_library_starts_empty()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        File.WriteAllText(_file, "{ это не json");

        var library = NewLibrary();
        Assert.Empty(library.List());
        Assert.True(File.Exists(_file + ".broken"));

        library.Add(Lineup());
        Assert.Single(NewLibrary().List());
    }
}
