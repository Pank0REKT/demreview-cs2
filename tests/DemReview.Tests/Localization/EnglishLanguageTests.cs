using System.Text.RegularExpressions;
using DemReview.Application.Context;
using DemReview.Application.Export;
using DemReview.Application.Grenades;
using DemReview.Application.Localization;
using DemReview.Application.Patterns;
using DemReview.Application.Routes;
using DemReview.Application.Situations;
using DemReview.Domain;
using DemReview.Tests.Context;

namespace DemReview.Tests.Localization;

/// <summary>Язык — глобальное состояние: тесты, переключающие его, не выполняются параллельно с остальными.</summary>
[CollectionDefinition(nameof(LanguageSwitching), DisableParallelization = true)]
public sealed class LanguageSwitching;

[Collection(nameof(LanguageSwitching))]
public class EnglishLanguageTests
{
    private static readonly Regex Cyrillic = new("[А-Яа-яЁё]");

    private static PlayerPatternService Service()
    {
        var routes = new RouteAnalysisService();
        var contexts = new RoundContextService();
        return new PlayerPatternService(new SituationAnalysisService(contexts, routes), routes, new GrenadeAnalysisService(), contexts);
    }

    /// <summary>Матч, в котором срабатывают паттерны нескольких категорий.</summary>
    private static MatchData Match()
    {
        string[] usual = ["TSpawn", "Ramp", "Palace", "BombsiteA"];
        var match = new SyntheticMatch().FiveVsFive().Rounds(6);
        for (var r = 1; r <= 6; r++)
        {
            match.Route(1, r, usual).Grenade(1, r, GrenadeType.Smoke).Damage(r, 8, 1, 6);
            if (r <= 4)
                match.Kill(r, 25, 6, 1);
        }
        return match.Build();
    }

    private static T InEnglish<T>(Func<T> action)
    {
        Loc.Language = UiLanguage.English;
        try
        {
            return action();
        }
        finally
        {
            Loc.Language = UiLanguage.Russian;
        }
    }

    [Fact]
    public void Profile_in_english_has_no_russian_text()
    {
        var profile = InEnglish(() => Service().BuildProfile(Match(), 1, new PatternOptions(MinShare: 0.5, MinExamples: 2)));

        Assert.NotEmpty(profile.Patterns);
        Assert.Contains(profile.Patterns, p => p.Text == "On T: in 6 of 6 rounds takes TSpawn → Ramp → Palace");
        Assert.All(profile.Patterns, p => Assert.DoesNotMatch(Cyrillic, p.Text + p.Condition + p.Action));
        Assert.All(profile.Roles.SelectMany(r => r.Reasons), reason => Assert.DoesNotMatch(Cyrillic, reason));
    }

    [Fact]
    public void Markdown_export_follows_language()
    {
        var match = Match();
        var (english, russian) = (
            InEnglish(() => new ProfileExporter().Export(Service().BuildProfile(match, 1), match, ExportFormat.Markdown)),
            new ProfileExporter().Export(Service().BuildProfile(match, 1), match, ExportFormat.Markdown));

        Assert.StartsWith("# Player profile: p1", english);
        Assert.Contains("| Side | Rounds | K/D | ADR |", english);
        Assert.DoesNotMatch(Cyrillic, english);
        Assert.StartsWith("# Профиль игрока p1", russian);
    }

    [Fact]
    public void Frequency_tables_and_labels_follow_language()
    {
        var tables = InEnglish(() => new SituationAnalysisService(new RoundContextService(), new RouteAnalysisService())
            .Analyze(Match(), 1).Tables);

        Assert.Equal("Buy → route", tables[0].Name);
        Assert.All(tables.SelectMany(t => t.Rows), row => Assert.DoesNotMatch(Cyrillic, row.Condition + row.Decision));
        Assert.Equal("пистолетка", Labels.Buy(BuyType.Pistol));
    }

    [Fact]
    public void Language_codes_round_trip()
    {
        Assert.Equal(UiLanguage.English, Loc.FromCode(Loc.Code(UiLanguage.English)));
        Assert.Equal(UiLanguage.Russian, Loc.FromCode("RU"));
        Assert.Null(Loc.FromCode(null));
        Assert.Null(Loc.FromCode("de"));
    }
}
