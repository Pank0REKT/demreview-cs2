using System.Diagnostics;
using DemReview.Application.Analytics;
using DemReview.Application.Library;
using DemReview.Application.Localization;
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
    IDemoLibrary library,
    ISettingsService settings,
    IAnalytics analytics,
    ILogger<MatchLoader> logger) : IMatchLoader
{
    public async Task<MatchData> LoadAsync(string path, IProgress<ParseProgress>? progress, CancellationToken cancellationToken,
        bool bypassCache = false)
    {
        if (!File.Exists(path))
            throw new DemoParseException(Loc.T($"Файл не найден: {path}", $"File not found: {path}"));

        var options = new ParseOptions(settings.Current.SampleTickStep);
        var key = DemoFileKey.FromFile(path, options.SampleTickStep);
        // Чтение кеша (SQLite, распаковка) — тоже вне UI-потока.
        var cached = bypassCache ? null : await Task.Run(() => FromCacheOrLibrary(key), cancellationToken)
            .ConfigureAwait(false);
        if (cached is not null)
        {
            logger.LogInformation("Демка взята из кеша: {Path}", key.FullPath);
            progress?.Report(new ParseProgress(1, Loc.T("Загружено из кеша", "Loaded from cache")));
            TrackLoaded(cached, fromCache: true, parseTime: null);
            return cached;
        }

        logger.LogInformation("Разбор демки {Path} ({Length} байт), шаг {Step} тиков", key.FullPath, key.Length, options.SampleTickStep);

        // Разбор — CPU-bound работа, строго вне UI-потока.
        var stopwatch = Stopwatch.StartNew();
        var match = await Task.Run(() => parser.ParseAsync(key.FullPath, options, progress, cancellationToken), cancellationToken)
            .ConfigureAwait(false);

        cache.Store(key, match);
        logger.LogInformation("Разобрано: карта {Map}, раундов {Rounds}, игроков {Players}", match.MapName, match.Rounds.Count, match.Players.Count);
        TrackLoaded(match, fromCache: false, stopwatch.Elapsed);
        return match;
    }

    private MatchData? FromCacheOrLibrary(DemoFileKey key)
    {
        if (cache.TryGet(key, out var match))
            return match;
        if (!library.TryGet(key, out match))
            return null;
        cache.Store(key, match);
        return match;
    }

    /// <summary>Статистика: только карта, источник и время разбора — без пути к файлу и ников.</summary>
    private void TrackLoaded(MatchData match, bool fromCache, TimeSpan? parseTime)
    {
        var props = new Dictionary<string, object>
        {
            ["map"] = match.MapName,
            ["source"] = fromCache ? "cache" : "parse",
        };
        if (parseTime is { } time)
            props["parse_seconds"] = Math.Round(time.TotalSeconds, 1);
        analytics.Track(AnalyticsEvents.DemoLoaded, props);
    }
}
