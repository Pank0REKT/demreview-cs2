using DemReview.Application.Context;
using DemReview.Application.Grenades;
using DemReview.Application.Patterns;
using DemReview.Application.Routes;
using DemReview.Application.Situations;
using DemReview.Domain;
using DemReview.Tests.Context;

namespace DemReview.Tests.Patterns;

public class PlayerPatternTests
{
    private static PlayerPatternService Service()
    {
        var routes = new RouteAnalysisService();
        var contexts = new RoundContextService();
        return new PlayerPatternService(new SituationAnalysisService(contexts, routes), routes, new GrenadeAnalysisService(), contexts);
    }

    /// <summary>Пять раундов за T; игрок 1 идёт заданными маршрутами.</summary>
    private static SyntheticMatch WithRoutes(params string[][] routes)
    {
        var match = new SyntheticMatch().FiveVsFive().Rounds(routes.Length);
        for (var i = 0; i < routes.Length; i++)
            match.Route(1, i + 1, routes[i]);
        return match;
    }

    [Fact]
    public void Repeated_route_above_threshold_becomes_pattern()
    {
        string[] usual = ["TSpawn", "Ramp", "Palace", "BombsiteA"];
        var match = WithRoutes(usual, usual, usual, usual, ["TSpawn", "Mid", "Window"]).Build();

        var profile = Service().BuildProfile(match, 1);

        var route = Assert.Single(profile.Patterns, p => p.Category == PatternCategory.Route && p.Action.Contains("Ramp"));
        Assert.Equal("На T в 4 из 5 раундов идёт TSpawn → Ramp → Palace", route.Text);
        Assert.Equal([1, 2, 3, 4], route.Rounds);
        Assert.Equal(0.8, route.Share, 3);
    }

    [Fact]
    public void Rare_behavior_is_hidden_and_threshold_is_configurable()
    {
        string[] a = ["TSpawn", "Ramp", "Palace"], b = ["TSpawn", "Mid", "Window"], c = ["TSpawn", "Apps", "B"];
        string[] d = ["TSpawn", "Short", "A"];
        var match = WithRoutes(a, a, a, b, c, d).Build(); // 3 из 6 = 50% < 60%

        Assert.DoesNotContain(Service().BuildProfile(match, 1).Patterns, p => p.Category == PatternCategory.Route);
        Assert.Contains(Service().BuildProfile(match, 1, new PatternOptions(MinShare: 0.5)).Patterns,
            p => p.Category == PatternCategory.Route && p.Count == 3);
    }

    [Fact]
    public void Pattern_with_too_few_examples_is_hidden()
    {
        string[] a = ["TSpawn", "Ramp", "Palace"];
        var match = WithRoutes(a, a).Build();

        // 2 из 2 = 100%, но примеров меньше трёх.
        Assert.DoesNotContain(Service().BuildProfile(match, 1).Patterns, p => p.Category == PatternCategory.Route);
        Assert.Contains(Service().BuildProfile(match, 1, new PatternOptions(MinExamples: 2)).Patterns,
            p => p.Category == PatternCategory.Route);
    }

    [Fact]
    public void Ct_rotation_after_teammate_death_is_detected()
    {
        // Игрок 6 (CT) держит BombsiteA, после смерти тиммейта на 18 с уходит через Connector (25 с).
        string[] rotate = ["CTSpawn", "BombsiteA", "BombsiteA", "BombsiteA", "BombsiteA", "Connector", "Mid", "Mid", "Mid"];
        string[] stay = ["CTSpawn", "BombsiteA", "BombsiteA", "BombsiteA", "BombsiteA", "BombsiteA", "BombsiteA", "BombsiteA"];
        var match = new SyntheticMatch().FiveVsFive().Rounds(4)
            .ZonePosition("BombsiteA", 0, 0).ZonePosition("Connector", 1500, 0).ZonePosition("Mid", 2500, 0)
            .ZonePosition("CTSpawn", -300, -300)
            .Route(6, 1, rotate).Route(6, 2, rotate).Route(6, 3, rotate).Route(6, 4, stay)
            .Kill(1, 18, 1, 7).Kill(2, 18, 1, 7).Kill(3, 18, 1, 7).Kill(4, 18, 1, 7)
            .Build();

        var rotation = Assert.Single(Service().BuildProfile(match, 6).Patterns, p => p.Category == PatternCategory.Rotation);

        Assert.Equal(3, rotation.Count);
        Assert.Equal(4, rotation.Total);
        Assert.Contains("смерть тиммейта", rotation.Text);
        Assert.Contains("уходит с BombsiteA через Connector", rotation.Text);
        Assert.Equal([1, 2, 3], rotation.Rounds);
    }

    [Fact]
    public void Repeated_death_spot_is_detected()
    {
        string[] window = ["TSpawn", "Mid", "Window"];
        var match = WithRoutes(window, window, window, window, ["TSpawn", "Ramp"])
            .Kill(1, 12, 7, 1).Kill(2, 12, 7, 1).Kill(3, 12, 7, 1)
            .Build();

        var death = Assert.Single(Service().BuildProfile(match, 1).Patterns, p => p.Category == PatternCategory.Deaths);
        Assert.Equal("На T погибает в Window в 3 из 4 раундов, когда туда заходит", death.Text);
    }

    [Fact]
    public void Identical_behavior_is_fully_predictable()
    {
        string[] same = ["TSpawn", "Ramp", "Palace"];
        var match = WithRoutes(same, same, same, same).Build();

        Assert.Equal(100, Service().BuildProfile(match, 1).PredictabilityPercent);
    }

    [Fact]
    public void Varied_behavior_is_less_predictable()
    {
        var match = WithRoutes(["TSpawn", "A"], ["TSpawn", "B"], ["TSpawn", "C"], ["TSpawn", "D"]).Build();

        Assert.InRange(Service().BuildProfile(match, 1).PredictabilityPercent, 0, 80);
    }

    [Fact]
    public void Player_with_most_first_duels_is_entry()
    {
        var match = new SyntheticMatch().FiveVsFive().Rounds(5)
            .Kill(1, 10, 1, 6).Kill(2, 10, 7, 1).Kill(3, 10, 1, 8).Kill(4, 10, 2, 9).Kill(5, 10, 1, 10)
            .Build();

        var role = Service().BuildProfile(match, 1).Roles.Single(r => r.Side == Side.T);
        Assert.Equal(PlayerRole.Entry, role.Role);
        Assert.NotEmpty(role.Reasons);
    }

    [Theory]
    [InlineData(0, 0, 0.0)]
    [InlineData(3, 3, 0.438)]
    [InlineData(5, 5, 0.566)]
    [InlineData(4, 5, 0.376)]
    public void Wilson_lower_bound_accounts_for_small_samples(int successes, int total, double expected)
    {
        Assert.Equal(expected, PatternStats.WilsonLowerBound(successes, total), 2);
    }

    [Fact]
    public void Confidence_grows_with_sample_size()
    {
        Assert.True(PatternStats.WilsonLowerBound(12, 15) > PatternStats.WilsonLowerBound(4, 5));
        Assert.Equal(Confidence.Low, PatternStats.Level(PatternStats.WilsonLowerBound(2, 3)));
        Assert.Equal(Confidence.High, PatternStats.Level(PatternStats.WilsonLowerBound(10, 10)));
    }
}
