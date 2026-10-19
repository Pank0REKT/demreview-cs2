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
