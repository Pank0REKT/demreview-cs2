using DemReview.Domain;
using Microsoft.Extensions.Logging;

namespace DemReview.Application.Parsing;

public interface IMatchLoader
{
    /// <summary>Загружает демку из кеша или разбирает её в фоновом потоке.</summary>
    /// <exception cref="DemoParseException">Понятная пользователю ошибка.</exception>
    /// <exception cref="OperationCanceledException">Загрузка отменена.</exception>
    Task<MatchData> LoadAsync(string path, IProgress<ParseProgress>? progress, CancellationToken cancellationToken, bool bypassCache = false);
}

public sealed class MatchLoader(
    IDemoParser parser,
    IMatchCache cache,
    ISettingsService settings,
    ILogger<MatchLoader> logger) : IMatchLoader
{
    public async Task<MatchData> LoadAsync(string path, IProgress<ParseProgress>? progress, CancellationToken cancellationToken,
        bool bypassCache = false)
    {
        if (!File.Exists(path))
            throw new DemoParseException($"Файл не найден: {path}");

        var options = new ParseOptions(settings.Current.SampleTickStep);
        var key = DemoFileKey.FromFile(path, options.SampleTickStep);
        // Чтение кеша (SQLite, распаковка) — тоже вне UI-потока.
        var cached = bypassCache ? null : await Task.Run(() => cache.TryGet(key, out var m) ? m : null, cancellationToken)
            .ConfigureAwait(false);
        if (cached is not null)
        {
            logger.LogInformation("Демка взята из кеша: {Path}", key.FullPath);
            progress?.Report(new ParseProgress(1, "Загружено из кеша"));
            return cached;
        }

        logger.LogInformation("Разбор демки {Path} ({Length} байт), шаг {Step} тиков", key.FullPath, key.Length, options.SampleTickStep);

        // Разбор — CPU-bound работа, строго вне UI-потока.
        var match = await Task.Run(() => parser.ParseAsync(key.FullPath, options, progress, cancellationToken), cancellationToken)
            .ConfigureAwait(false);

        cache.Store(key, match);
        logger.LogInformation("Разобрано: карта {Map}, раундов {Rounds}, игроков {Players}", match.MapName, match.Rounds.Count, match.Players.Count);
        return match;
    }
}
