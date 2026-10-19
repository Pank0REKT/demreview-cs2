using System.Numerics;
using DemReview.Application.Routes;
using DemReview.Domain;

namespace DemReview.Tests.Routes;

public class RouteAnalysisTests
{
    private const ulong Me = 1;

    /// <summary>Сэмплы раз в 0.25 с по списку (зона, секунды в ней).</summary>
    private static IEnumerable<PlayerSample> Walk(int round, params (string Zone, float Seconds)[] legs)
    {
        var t = 0f;
        var tick = round * 10_000;
        foreach (var (zone, seconds) in legs)
        {
            for (var s = 0f; s < seconds; s += 0.25f, t += 0.25f, tick += 16)
                yield return new PlayerSample(tick, round, t, new Vector3(t, 0, 0), 0, 0, 250, MovementFlags.None, "ak47", 100, 100, 0, 0, zone);
        }
    }

    private static MatchData Match(Side side, params IEnumerable<PlayerSample>[] rounds)
    {
        var samples = rounds.SelectMany(r => r).ToArray();
        var roundNumbers = samples.Select(s => s.Round).Distinct().ToList();
        var player = new PlayerInfo(Me, "me", false, side, "A", 0, 0, 0, roundNumbers.ToDictionary(r => r, _ => side));
        var roundInfos = roundNumbers.Select(r => new RoundInfo(r, 0, 0, 1, Side.T, RoundEndReason.TerroristsWin, 0, 0, false)).ToList();
        return new MatchData("x.dem", "de_mirage", 64, new MatchScore("A", 0, "B", 0), roundInfos, [player], [],
            new Dictionary<ulong, PlayerTrack> { [Me] = new(Me, samples) }, 16, [], [], []);
    }

    [Fact]
    public void Short_pass_through_zones_are_dropped()
    {
        var zones = RouteAnalysisService.BuildZones(
            Walk(1, ("TSpawn", 3), ("Corner", 0.5f), ("Ramp", 4), ("Palace", 2)).ToList(), minDwell: 1);

        Assert.Equal(["TSpawn", "Ramp", "Palace"], zones.Select(z => z.Zone));
    }

    [Fact]
    public void Same_zone_split_by_pass_through_is_merged()
    {
        var zones = RouteAnalysisService.BuildZones(
            Walk(1, ("TSpawn", 2), ("Mid", 3), ("Window", 0.25f), ("Mid", 3), ("CT", 2)).ToList(), minDwell: 1);

        Assert.Equal(["TSpawn", "Mid", "CT"], zones.Select(z => z.Zone));
    }

    [Fact]
    public void Arrival_time_is_first_sample_in_zone()
    {
        var zones = RouteAnalysisService.BuildZones(Walk(1, ("TSpawn", 2), ("Ramp", 3)).ToList(), minDwell: 1);

        Assert.Equal(0f, zones[0].Arrival);
        Assert.Equal(2f, zones[1].Arrival);
        Assert.Equal(2f, zones[0].Leave);
    }

    [Fact]
    public void Empty_place_names_are_ignored()
    {
        var zones = RouteAnalysisService.BuildZones(Walk(1, ("TSpawn", 2), ("", 3), ("Ramp", 2)).ToList(), minDwell: 1);

        Assert.Equal(["TSpawn", "Ramp"], zones.Select(z => z.Zone));
    }

    [Fact]
    public void Frequent_routes_group_rounds_by_route_prefix()
    {
        var match = Match(Side.T,
            Walk(1, ("TSpawn", 2), ("Ramp", 3), ("Palace", 3), ("A", 3)),
            Walk(2, ("TSpawn", 2), ("Ramp", 3), ("Palace", 3), ("CT", 3)),
            Walk(3, ("TSpawn", 2), ("Ramp", 3), ("Palace", 2)),
            Walk(4, ("TSpawn", 2), ("Mid", 3), ("B", 3)));

        var analysis = new RouteAnalysisService().Analyze(match, Me, new RouteOptions(PrefixLength: 3, MinRoundsForFrequent: 2));

        var frequent = Assert.Single(analysis.FrequentRoutes);
        Assert.Equal(["TSpawn", "Ramp", "Palace"], frequent.Zones);
        Assert.Equal([1, 2, 3], frequent.Rounds);
        Assert.Equal(0.75, frequent.Share, 3);
    }

    [Fact]
    public void Zone_timing_reports_median_arrival()
    {
        var match = Match(Side.T,
            Walk(1, ("TSpawn", 2), ("Ramp", 3)),
            Walk(2, ("TSpawn", 4), ("Ramp", 3)),
            Walk(3, ("TSpawn", 6), ("Ramp", 3)));

        var timing = new RouteAnalysisService().Analyze(match, Me).ZoneTimings.Single(t => t.Zone == "Ramp");

        Assert.Equal(3, timing.Rounds);
        Assert.Equal(4f, timing.MedianArrival);
        Assert.Equal(2f, timing.EarliestArrival);
        Assert.Equal(6f, timing.LatestArrival);
    }

    [Fact]
    public void Player_without_samples_has_empty_analysis()
    {
        var analysis = new RouteAnalysisService().Analyze(Match(Side.T), 999);

        Assert.Empty(analysis.Rounds);
        Assert.Empty(analysis.FrequentRoutes);
    }
}
