using DemReview.Application.Localization;
using DemReview.Application.Parsing;
using DemReview.Domain;
using Microsoft.Extensions.Logging;

namespace DemReview.Application.Library;

public interface IDemoLibraryService
{
    /// <summary>Разбирает в библиотеку все .dem из папки (и подпапок), которых там ещё нет или которые изменились.</summary>
    /// <exception cref="OperationCanceledException">Импорт отменён; уже разобранные демки остаются в библиотеке.</exception>
    Task<LibraryImportResult> ImportFolderAsync(string folder, IProgress<ParseProgress>? progress, CancellationToken cancellationToken);

    /// <summary>Сводный матч из демок библиотеки одной карты (данные читаются из библиотеки, файлы .dem не нужны).</summary>
    Task<MatchData> CombineAsync(IReadOnlyList<LibraryEntry> entries, CancellationToken cancellationToken);
}

/// <param name="Failed">Файлы, которые не удалось разобрать, и причина.</param>
public sealed record LibraryImportResult(int Found, int Added, int Skipped, IReadOnlyList<(string Path, string Error)> Failed);

public sealed class DemoLibraryService(
    IDemoLibrary library,
    IMatchCache cache,
    IDemoParser parser,
    ISettingsService settings,
    ILogger<DemoLibraryService> logger) : IDemoLibraryService
{
    private static readonly EnumerationOptions Search = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        MatchCasing = MatchCasing.CaseInsensitive,
    };

    public async Task<LibraryImportResult> ImportFolderAsync(string folder, IProgress<ParseProgress>? progress, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(folder))
            throw new DemoParseException(Loc.T($"Папка не найдена: {folder}", $"Folder not found: {folder}"));

        var files = await Task.Run(() => Directory.EnumerateFiles(folder, "*.dem", Search).Order(StringComparer.OrdinalIgnoreCase).ToList(),
            cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Импорт папки {Folder}: найдено демок {Count}", folder, files.Count);

        var options = new ParseOptions(settings.Current.SampleTickStep);
        var (added, skipped) = (0, 0);
        var failed = new List<(string, string)>();
        for (var i = 0; i < files.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = files[i];
            var name = Path.GetFileName(path);
            var index = i;
            void Report(double fraction, string stage) =>
                progress?.Report(new ParseProgress((index + fraction) / files.Count, $"{index + 1}/{files.Count} · {name}: {stage}"));

            try
            {
                var key = DemoFileKey.FromFile(path, options.SampleTickStep);
                var stored = await Task.Run(() =>
                {
                    if (library.Contains(key))
                        return false;
                    // Демка уже разобрана и лежит в кеше — разбирать заново не нужно.
                    if (!cache.TryGet(key, out var match))
                        return (bool?)null;
                    library.Store(key, match);
                    return true;
                }, cancellationToken).ConfigureAwait(false);

                if (stored == false)
                {
                    skipped++;
                    Report(1, Loc.T("уже в библиотеке", "already in the library"));
                    continue;
                }

                if (stored is null)
                {
                    var match = await Task.Run(() => parser.ParseAsync(key.FullPath, options, new Relay(p => Report(p.Fraction, p.Stage)), cancellationToken),
                        cancellationToken).ConfigureAwait(false);
                    await Task.Run(() => library.Store(key, match), cancellationToken).ConfigureAwait(false);
                }

                added++;
                Report(1, Loc.T("добавлена", "added"));
            }
            catch (DemoParseException ex)
            {
                failed.Add((path, ex.Message));
                logger.LogWarning(ex, "Не удалось разобрать {Path} при импорте папки", path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add((path, ex.Message));
                logger.LogWarning(ex, "Не удалось прочитать {Path} при импорте папки", path);
            }
        }

        logger.LogInformation("Импорт папки {Folder}: добавлено {Added}, пропущено {Skipped}, ошибок {Failed}", folder, added, skipped, failed.Count);
        return new LibraryImportResult(files.Count, added, skipped, failed);
    }

    public Task<MatchData> CombineAsync(IReadOnlyList<LibraryEntry> entries, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var sources = new List<MatchSource>();
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var match = library.Load(entry.Id) ?? throw new DemoParseException(Loc.T(
                $"Демки {entry.FileName} нет в библиотеке или она в старом формате — обновите папку, чтобы разобрать её заново.",
                $"{entry.FileName} is missing from the library or stored in an old format — rescan its folder to parse it again."));
            sources.Add(new MatchSource(match, entry.FileTimeUtc));
        }

        return MatchMerger.Merge(sources);
    }, cancellationToken);

    /// <summary>Прогресс разбора одной демки, пересчитанный в общий прогресс импорта (синхронно, без контекста синхронизации).</summary>
    private sealed class Relay(Action<ParseProgress> handler) : IProgress<ParseProgress>
    {
        public void Report(ParseProgress value) => handler(value);
    }
}
