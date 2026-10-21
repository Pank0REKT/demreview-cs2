using DemReview.Application.Context;
using DemReview.Application.Grenades;
using DemReview.Application.Library;
using DemReview.Application.Patterns;
using DemReview.Application.Routes;
using DemReview.Application.Situations;
using DemReview.Domain;
using DemReview.Tests.Context;

namespace DemReview.Tests.Library;

public class MatchMergerTests
{
    private static readonly DateTime Day = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Три раунда за T; игрок 1 идёт через Ramp, бросает дым и убивает игрока 6 в первом раунде.</summary>
    private static MatchData Demo(string path, string map = "de_mirage")
    {
        var builder = new SyntheticMatch().FiveVsFive().Rounds(3);
        for (var r = 1; r <= 3; r++)
            builder.Route(1, r, "TSpawn", "Ramp", "Palace");
        return builder.Kill(1, 10, 1, 6).Grenade(1, 2, GrenadeType.Smoke).Bomb(3, 30, BombEventType.Planted).Damage(2, 5, 1, 7).Build()
            with { SourcePath = path, MapName = map };
    }

    [Fact]
    public void Rounds_are_numbered_consecutively_and_ticks_do_not_overlap()
    {
        var merged = MatchMerger.Merge([new(Demo("b.dem"), Day.AddDays(1)), new(Demo("a.dem"), Day)]);

        Assert.Equal([1, 2, 3, 4, 5, 6], merged.Rounds.Select(r => r.Number));
        for (var i = 1; i < merged.Rounds.Count; i++)
            Assert.True(merged.Rounds[i].StartTick > merged.Rounds[i - 1].EndTick);

        // Демки — по времени игры: a.dem раньше.
        Assert.Equal(["a.dem", "b.dem"], merged.Parts.Select(p => p.FileName));
        Assert.Equal((4, 6, 3), (merged.Parts[1].FirstRound, merged.Parts[1].LastRound, merged.Parts[1].RoundOffset));
        Assert.Equal(merged.Parts[1], merged.PartOf(5));

        Assert.Equal([1, 4], merged.Kills.Select(k => k.Round));
        Assert.Equal([2, 5], merged.Damages.Select(d => d.Round));
        Assert.Equal([3, 6], merged.Bombs.Select(b => b.Round));
        Assert.Equal(2, merged.Grenades.Select(g => g.Id).Distinct().Count());
        Assert.All(merged.Kills, k => Assert.True(merged.FindRound(k.Round)!.ContainsTick(k.Tick)));

        var track = merged.TrackOf(1).Samples;
        Assert.Equal(track.OrderBy(s => s.Tick).Select(s => s.Tick), track.Select(s => s.Tick));
        Assert.Equal([1, 2, 3, 4, 5, 6], track.Select(s => s.Round).Distinct());
    }

    [Fact]
    public void Same_player_is_one_player_with_summed_stats()
    {
        var a = Demo("a.dem");
        var withStats = a with { Players = a.Players.Select(p => p.SteamId == 1 ? p with { Kills = 20, Deaths = 10, Assists = 3 } : p).ToList() };

        var merged = MatchMerger.Merge([new(withStats, Day), new(withStats, Day.AddDays(1))]);

        Assert.Equal(10, merged.Players.Count);
        var player = merged.FindPlayer(1)!;
        Assert.Equal((40, 20, 6), (player.Kills, player.Deaths, player.Assists));
        Assert.Equal(6, player.SideByRound.Count);
        Assert.Equal(Side.T, player.SideInRound(5));
    }

    [Fact]
    public void Bots_with_the_same_name_in_different_demos_are_different_players()
    {
        MatchData WithBot(string path)
        {
            var demo = Demo(path);
            return demo with { Players = demo.Players.Select(p => p.SteamId == 10 ? p with { IsBot = true } : p).ToList() };
        }

        var merged = MatchMerger.Merge([new(WithBot("a.dem"), Day), new(WithBot("b.dem"), Day.AddDays(1))]);

        Assert.Equal(11, merged.Players.Count);
        Assert.Equal(3, merged.FindPlayer(10)!.SideByRound.Count);
    }

    [Fact]
    public void Different_maps_are_not_merged()
    {
        Assert.Throws<ArgumentException>(() => MatchMerger.Merge([new(Demo("a.dem"), Day), new(Demo("b.dem", "de_nuke"), Day)]));
    }

    [Fact]
    public void First_round_of_every_demo_is_a_pistol_round()
    {
        var merged = MatchMerger.Merge([new(Demo("a.dem"), Day), new(Demo("b.dem"), Day.AddDays(1))]);

        var context = new RoundContextService().Create(merged);

        Assert.Equal([1, 4], merged.Rounds.Where(r => context.Economy(r.Number)!.IsPistol).Select(r => r.Number));
    }

    [Fact]
    public void Profile_counts_rounds_of_all_demos()
    {
        var routes = new RouteAnalysisService();
        var contexts = new RoundContextService();
        var service = new PlayerPatternService(new SituationAnalysisService(contexts, routes), routes, new GrenadeAnalysisService(), contexts);
        var merged = MatchMerger.Merge([new(Demo("a.dem"), Day), new(Demo("b.dem"), Day.AddDays(1)), new(Demo("c.dem"), Day.AddDays(2))]);

        var profile = service.BuildProfile(merged, 1);

        Assert.Equal(9, profile.RoundsAnalyzed);
        var route = Assert.Single(profile.Patterns, p => p.Category == PatternCategory.Route && p.Action.Contains("Ramp"));
        Assert.Equal((9, 9), (route.Count, route.Total));
        Assert.Equal(Enumerable.Range(1, 9), route.Rounds);
    }
}
