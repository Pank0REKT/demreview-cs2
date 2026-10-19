using System.Text.Json;
using DemReview.Application.Context;
using DemReview.Application.Export;
using DemReview.Application.Grenades;
using DemReview.Application.Patterns;
using DemReview.Application.Routes;
using DemReview.Application.Situations;
using DemReview.Domain;
using DemReview.Tests.Context;

namespace DemReview.Tests.Patterns;

public class ProfileExporterTests
{
    private static (PlayerProfile Profile, MatchData Match) Sample()
    {
        string[] route = ["TSpawn", "Ramp", "Palace"];
        var builder = new SyntheticMatch().FiveVsFive().Rounds(4);
        for (var r = 1; r <= 4; r++)
            builder.Route(1, r, route);
        var match = builder.Build();

        var routes = new RouteAnalysisService();
        var contexts = new RoundContextService();
        var service = new PlayerPatternService(new SituationAnalysisService(contexts, routes), routes, new GrenadeAnalysisService(), contexts);
        return (service.BuildProfile(match, 1), match);
    }

    private readonly ProfileExporter _exporter = new();

    [Fact]
    public void Markdown_contains_role_patterns_and_predictability()
    {
        var (profile, match) = Sample();

        var md = _exporter.Export(profile, match, ExportFormat.Markdown);

        Assert.StartsWith("# Профиль игрока p1", md);
        Assert.Contains("## Роль", md);
        Assert.Contains($"**Предсказуемость:** {profile.PredictabilityPercent}%", md);
        Assert.Contains("На T в 4 из 4 раундов идёт TSpawn → Ramp → Palace", md);
        Assert.Contains("| T | 4 |", md);
    }

    [Fact]
    public void Json_is_valid_and_keeps_cyrillic_readable()
    {
        var (profile, match) = Sample();

        var json = _exporter.Export(profile, match, ExportFormat.Json);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("demreview-profile", root.GetProperty("format").GetString());
        Assert.Equal("1", root.GetProperty("player").GetProperty("steamId").GetString());
        Assert.Equal(profile.Patterns.Count, root.GetProperty("patterns").GetArrayLength());
        Assert.All(root.GetProperty("patterns").EnumerateArray(), p =>
            Assert.Matches("^[a-z][A-Za-z]*$", p.GetProperty("category").GetString()!));
        Assert.Contains(root.GetProperty("patterns").EnumerateArray(), p => p.GetProperty("category").GetString() == "route");
        Assert.Contains("идёт", json);          // не ид...
        Assert.DoesNotContain("\\u04", json);
    }

    [Theory]
    [InlineData("profile.json", ExportFormat.Json)]
    [InlineData("PROFILE.JSON", ExportFormat.Json)]
    [InlineData("profile.md", ExportFormat.Markdown)]
    [InlineData("профиль игрока", ExportFormat.Markdown)]
    public void Format_is_chosen_by_extension(string path, ExportFormat expected)
    {
        Assert.Equal(expected, _exporter.FormatFor(path));
    }

    [Fact]
    public void Markdown_escapes_special_characters_in_names()
    {
        var (profile, match) = Sample();
        var md = _exporter.Export(profile with { Name = "x_*nick*_|1" }, match, ExportFormat.Markdown);

        Assert.Contains(@"x\_\*nick\*\_\|1", md);
    }
}
