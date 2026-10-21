using DemReview.Application;
using DemReview.Application.Library;
using DemReview.Application.Parsing;
using DemReview.Domain;
using DemReview.Infrastructure.Storage;
using DemReview.Tests.Context;
using DemReview.Tests.Parsing;
using Microsoft.Data.Sqlite;

namespace DemReview.Tests.Library;

public sealed class DemoLibraryServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(AppContext.BaseDirectory, "library-service-tests", Guid.NewGuid().ToString("N"));
    private readonly string _demos;
    private readonly SqliteDemoLibrary _library;
    private readonly FakeParser _parser = new();
    private readonly DemoLibraryService _service;

    public DemoLibraryServiceTests()
    {
        _demos = Path.Combine(_dir, "демки");
        Directory.CreateDirectory(Path.Combine(_demos, "старые"));
        var platform = DemReview.Infrastructure.InfrastructureServiceCollectionExtensions.CreatePlatformService();
        _library = new SqliteDemoLibrary(platform, new CollectingLogger<SqliteDemoLibrary>(), Path.Combine(_dir, "library.db"));
        _service = new DemoLibraryService(_library, new InMemoryMatchCache(platform), _parser, new FixedSettings(),
            new CollectingLogger<DemoLibraryService>());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private string Demo(string relativePath)
    {
        var path = Path.Combine(_demos, relativePath);
        File.WriteAllText(path, relativePath);
        return path;
    }

    [Fact]
    public async Task Folder_is_imported_once_including_subfolders()
    {
        Demo("a.dem");
        Demo(Path.Combine("старые", "b.DEM"));
        Demo("broken.dem");
        Demo("notes.txt");

        var first = await _service.ImportFolderAsync(_demos, null, TestContext.Current.CancellationToken);

        Assert.Equal((3, 2, 0), (first.Found, first.Added, first.Skipped));
        Assert.Equal("broken.dem", Path.GetFileName(Assert.Single(first.Failed).Path));
        Assert.Equal(2, _library.List().Count);

        // Повторный импорт ничего не разбирает: демки уже в библиотеке.
        _parser.Calls = 0;
        var second = await _service.ImportFolderAsync(_demos, null, TestContext.Current.CancellationToken);
        Assert.Equal((0, 2), (second.Added, second.Skipped));
        Assert.Equal(1, _parser.Calls); // только битая демка
    }

    [Fact]
    public async Task Combined_match_is_built_from_the_library_without_files()
    {
        Demo("a.dem");
        Demo("b.dem");
        await _service.ImportFolderAsync(_demos, null, TestContext.Current.CancellationToken);
        Directory.Delete(_demos, recursive: true);

        var match = await _service.CombineAsync(_library.List(), TestContext.Current.CancellationToken);

        Assert.True(match.IsCombined);
        Assert.Equal(2, match.Parts.Count);
        Assert.Equal(6, match.Rounds.Count);
    }

    private sealed class FakeParser : IDemoParser
    {
        public int Calls { get; set; }

        public Task<MatchData> ParseAsync(string path, ParseOptions options, IProgress<ParseProgress>? progress, CancellationToken cancellationToken)
        {
            Calls++;
            if (Path.GetFileName(path) == "broken.dem")
                throw new DemoParseException("не демка");
            progress?.Report(new ParseProgress(0.5, "разбор"));
            return Task.FromResult(new SyntheticMatch().FiveVsFive().Rounds(3).Build() with { SourcePath = path });
        }
    }

    private sealed class FixedSettings : ISettingsService
    {
        public AppSettings Current { get; private set; } = new();

        public void Update(Func<AppSettings, AppSettings> change) => Current = change(Current);
    }
}
