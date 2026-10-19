using DemReview.Application.Parsing;
using DemReview.Domain;
using DemReview.Infrastructure.Parsing;
using Microsoft.Extensions.Logging;

namespace DemReview.Tests.Parsing;

/// <summary>
/// Проверка парсера на реальных демках из папки samples/ (в .gitignore).
/// Если демок нет — тесты пропускаются.
/// </summary>
public class SampleDemosTests
{
    public static TheoryData<string> Demos()
    {
        var data = new TheoryData<string>();
        if (TestPaths.SamplesDirectory is { } dir)
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.dem").Order())
                data.Add(Path.GetFileName(file));
        }
        else
        {
            data.Add("");
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Demos))]
    public async Task Sample_demo_parses_consistently(string fileName)
    {
        Assert.SkipWhen(fileName.Length == 0, "Нет демок в samples/");

        var logger = new CollectingLogger<DemoFileDemoParser>();
        var parser = new DemoFileDemoParser(logger);
        var path = Path.Combine(TestPaths.SamplesDirectory!, fileName);

        var match = await parser.ParseAsync(path, new ParseOptions(), null, TestContext.Current.CancellationToken);

        Assert.StartsWith("de_", match.MapName);
        Assert.Equal(match.FinalScore.TeamTScore + match.FinalScore.TeamCTScore, match.Rounds.Count);
        Assert.Equal(Enumerable.Range(1, match.Rounds.Count), match.Rounds.Select(r => r.Number));
        Assert.All(match.Rounds, r => Assert.NotEqual(Side.None, r.Winner));
        Assert.Equal(10, match.Players.Count);
        Assert.Equal(5, match.Players.Count(p => p.FinalSide == Side.T));
        Assert.All(match.Players, p => Assert.NotEqual(0UL, p.SteamId));

        // K/D/A по событиям совпадает с табло игры.
        Assert.DoesNotContain(logger.Messages, m => m.Level >= LogLevel.Warning && m.Text.Contains("K/D/A"));

        // Позиции: у каждого игрока есть сэмплы, шаг ~16 тиков, у большинства есть название зоны.
        Assert.All(match.Players, p => Assert.NotEmpty(match.TrackOf(p.SteamId).Samples));
        var samples = match.Tracks.Values.SelectMany(t => t.Samples).ToList();
        Assert.True(samples.Count(s => s.Place.Length > 0) > samples.Count * 0.9, "Слишком много сэмплов без названия зоны");
        Assert.All(samples, s => Assert.InRange(s.TimeInRound, 0, 200));

        // Гранаты: бросившие — игроки матча, бросок связан с детонацией.
        var grenades = match.Grenades;
        Assert.NotEmpty(grenades);
        Assert.All(grenades, g => Assert.NotNull(match.FindPlayer(g.ThrowerSteamId)));
        Assert.All(grenades, g => Assert.NotEqual(Side.None, g.ThrowerSide));
        Assert.All(grenades, g => Assert.True(g.ThrowTime >= 0, $"Бросок #{g.Id} раньше конца фризтайма"));

        var instant = grenades.Where(g => g.Type is GrenadeType.HighExplosive or GrenadeType.Flashbang or GrenadeType.Smoke).ToList();
        Assert.True(instant.Count(g => g.Detonated) >= instant.Count * 0.95,
            $"Детонация найдена у {instant.Count(g => g.Detonated)} из {instant.Count} HE/flash/smoke");

        var fire = grenades.Where(g => g.Type is GrenadeType.Molotov or GrenadeType.Incendiary).ToList();
        Assert.True(fire.Count(g => g.Detonated) >= fire.Count * 0.8,
            $"Возгорание найдено у {fire.Count(g => g.Detonated)} из {fire.Count} молотовых");

        // Детонация недалеко по времени от броска (граната не «склеилась» с чужой).
        Assert.All(grenades.Where(g => g.Detonated), g => Assert.InRange(g.DetonateTick!.Value - g.ThrowTick, 0, 64 * 25));

        // Экономика: пистолетки — 1-й раунд и первый после смены сторон (MR12 → 13-й).
        var context = new DemReview.Application.Context.RoundContextService().Create(match);
        var pistols = match.Rounds.Where(r => context.Economy(r.Number)!.IsPistol).Select(r => r.Number).ToList();
        Assert.Equal(match.Rounds.Count >= 13 ? [1, 13] : [1], pistols);

        // Бомба: каждая обезвреженная/взорванная — после закладки в том же раунде, точка известна.
        Assert.All(match.Bombs.Where(b => b.Type != BombEventType.Planted), b =>
            Assert.Contains(match.Bombs, p => p.Type == BombEventType.Planted && p.Round == b.Round && p.Tick < b.Tick));
        Assert.All(match.Bombs, b => Assert.NotEqual(BombSite.Unknown, b.Site));
        Assert.NotEmpty(match.Damages);

        var flashes = grenades.Where(g => g.Type == GrenadeType.Flashbang).ToList();
        Assert.True(flashes.Count(f => f.FlashVictims.Count > 0) >= flashes.Count / 3, "Слишком мало флешек с ослеплёнными");
    }

    /// <summary>
    /// Калибровка радара: все позиции игроков из демки должны попадать в изображение 1024×1024,
    /// а спавны — в ожидаемые части радара.
    /// </summary>
    [Theory]
    [MemberData(nameof(Demos))]
    public async Task Radar_calibration_fits_player_positions(string fileName)
    {
        Assert.SkipWhen(fileName.Length == 0, "Нет демок в samples/");

        var parser = new DemoFileDemoParser(new CollectingLogger<DemoFileDemoParser>());
        var match = await parser.ParseAsync(Path.Combine(TestPaths.SamplesDirectory!, fileName), new ParseOptions(), null,
            TestContext.Current.CancellationToken);
        var map = new DemReview.Infrastructure.Maps.EmbeddedMapConfigProvider().Find(match.MapName);
        Assert.SkipWhen(map is null, $"Карта {match.MapName} не описана в maps.json");

        var converter = new DemReview.Application.Maps.MapCoordinateConverter();
        var points = match.Tracks.Values.SelectMany(t => t.Samples).Select(s => converter.ToRadar(map!, s.Position)).ToList();

        Assert.All(points, p =>
        {
            Assert.InRange(p.X, 0, 1024);
            Assert.InRange(p.Y, 0, 1024);
        });

        // Точки занимают заметную часть радара (масштаб не завышен в разы).
        var width = points.Max(p => p.X) - points.Min(p => p.X);
        var height = points.Max(p => p.Y) - points.Min(p => p.Y);
        Assert.True(Math.Max(width, height) > 600, $"Позиции занимают только {width:F0}×{height:F0} px");
    }
}

internal static class TestPaths
{
    /// <summary>Папка samples/ в корне репозитория (ищется вверх от каталога тестов).</summary>
    public static string? SamplesDirectory { get; } = FindSamples();

    private static string? FindSamples()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DemReview.slnx")))
            {
                var samples = Path.Combine(dir.FullName, "samples");
                return Directory.Exists(samples) && Directory.EnumerateFiles(samples, "*.dem").Any() ? samples : null;
            }
        }
        return null;
    }
}

internal sealed class CollectingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Text)> Messages { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (Messages)
            Messages.Add((logLevel, formatter(state, exception)));
    }
}
