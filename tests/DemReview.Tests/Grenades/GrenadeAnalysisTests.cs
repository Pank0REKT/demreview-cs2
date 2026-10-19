using System.Numerics;
using DemReview.Application.Grenades;
using DemReview.Domain;
using DemReview.Infrastructure.Parsing;

namespace DemReview.Tests.Grenades;

public class GrenadeAnalysisTests
{
    private const ulong Me = 1, Mate = 2, Enemy = 3;
    private static int _nextId;

    private static GrenadeThrow Throw(GrenadeType type, Vector3 from, Vector3? to, int round = 1, Side side = Side.T,
        float time = 10, string place = "TSpawn", params FlashVictim[] victims) =>
        new(Interlocked.Increment(ref _nextId), type, Me, side, round, round * 1000, time, from, 0, 0, place, from,
            to.HasValue ? round * 1000 + 100 : null, to, victims);

    private static MatchData Match(params GrenadeThrow[] grenades)
    {
        var player = new PlayerInfo(Me, "me", false, Side.T, "A", 0, 0, 0, new Dictionary<int, Side> { [1] = Side.T });
        return new MatchData("x.dem", "de_mirage", 64, new MatchScore("A", 0, "B", 0), [], [player], [],
            new Dictionary<ulong, PlayerTrack>(), 16, grenades, [], []);
    }

    [Fact]
    public void Same_lineup_in_different_rounds_forms_cluster()
    {
        var a = Throw(GrenadeType.Smoke, new(0, 0, 0), new(1000, 1000, 0), round: 1);
        var b = Throw(GrenadeType.Smoke, new(50, 30, 0), new(1100, 950, 0), round: 3);
        var c = Throw(GrenadeType.Smoke, new(-40, 20, 0), new(980, 1060, 0), round: 7);

        var clusters = GrenadeAnalysisService.Cluster([a, b, c], new GrenadeClusterOptions());

        var cluster = Assert.Single(clusters);
        Assert.Equal(3, cluster.Count);
    }

    [Fact]
    public void Different_landing_spot_is_not_clustered()
    {
        var a = Throw(GrenadeType.Smoke, new(0, 0, 0), new(1000, 1000, 0));
        var b = Throw(GrenadeType.Smoke, new(20, 0, 0), new(2000, -500, 0));

        Assert.Empty(GrenadeAnalysisService.Cluster([a, b], new GrenadeClusterOptions()));
    }

    [Fact]
    public void Different_grenade_types_and_sides_are_not_clustered()
    {
        var smoke = Throw(GrenadeType.Smoke, new(0, 0, 0), new(1000, 1000, 0));
        var flash = Throw(GrenadeType.Flashbang, new(0, 0, 0), new(1000, 1000, 0));
        var ctSmoke = Throw(GrenadeType.Smoke, new(0, 0, 0), new(1000, 1000, 0), side: Side.CT);

        Assert.Empty(GrenadeAnalysisService.Cluster([smoke, flash, ctSmoke], new GrenadeClusterOptions()));
    }

    [Fact]
    public void Threshold_is_configurable()
    {
        var a = Throw(GrenadeType.HighExplosive, new(0, 0, 0), new(1000, 0, 0));
        var b = Throw(GrenadeType.HighExplosive, new(0, 0, 0), new(1400, 0, 0));

        Assert.Empty(GrenadeAnalysisService.Cluster([a, b], new GrenadeClusterOptions(LandDistance: 250)));
        Assert.Single(GrenadeAnalysisService.Cluster([a, b], new GrenadeClusterOptions(LandDistance: 500)));
    }

    [Fact]
    public void Undetonated_grenades_are_not_clustered()
    {
        var a = Throw(GrenadeType.Molotov, new(0, 0, 0), null);
        var b = Throw(GrenadeType.Molotov, new(0, 0, 0), null);

        Assert.Empty(GrenadeAnalysisService.Cluster([a, b], new GrenadeClusterOptions()));
    }

    [Fact]
    public void Flash_summary_separates_enemies_teammates_and_self()
    {
        var enemyFlash = Throw(GrenadeType.Flashbang, new(0, 0, 0), new(500, 0, 0),
            victims: [new FlashVictim(Enemy, 2.5f, false, false), new FlashVictim(Mate, 1f, true, false)]);
        var selfFlash = Throw(GrenadeType.Flashbang, new(0, 0, 0), new(100, 0, 0), victims: [new FlashVictim(Me, 3f, true, true)]);
        var empty = Throw(GrenadeType.Flashbang, new(0, 0, 0), new(800, 0, 0));

        var flashes = new GrenadeAnalysisService().Analyze(Match(enemyFlash, selfFlash, empty), Me).Flashes;

        Assert.Equal(3, flashes.Thrown);
        Assert.Equal(1, flashes.EnemiesBlinded);
        Assert.Equal(2.5f, flashes.EnemyBlindSeconds);
        Assert.Equal(1, flashes.TeammatesBlinded);
        Assert.Equal(1, flashes.SelfBlinded);
        Assert.Equal(2, flashes.WithoutEnemyVictims);
    }

    [Fact]
    public void Summary_groups_by_type_and_side_with_median_time()
    {
        var match = Match(
            Throw(GrenadeType.Smoke, new(0, 0, 0), new(1, 1, 0), round: 1, time: 5),
            Throw(GrenadeType.Smoke, new(0, 0, 0), new(1, 1, 0), round: 2, time: 15),
            Throw(GrenadeType.Smoke, new(0, 0, 0), new(1, 1, 0), round: 2, time: 25));

        var summary = Assert.Single(new GrenadeAnalysisService().Analyze(match, Me).Summary);

        Assert.Equal(3, summary.Count);
        Assert.Equal(2, summary.Rounds);
        Assert.Equal(15f, summary.MedianThrowTime);
    }
}

public class MatchBuilderGrenadeTests
{
    private static MatchBuilder StartedRound(int number = 1)
    {
        var b = new MatchBuilder();
        b.RoundStart(0);
        b.FreezeEnd(640, number, 0, 0, false, false,
            [new(1, "me", false, Side.T), new(2, "mate", false, Side.T), new(3, "enemy", false, Side.CT)]);
        return b;
    }

    [Fact]
    public void Throw_time_is_counted_from_freeze_end()
    {
        var b = StartedRound();
        b.ThrowGrenade(GrenadeType.Smoke, 1, 640 + 64 * 10, Vector3.Zero, 0, 0, "TSpawn", Vector3.Zero);

        var g = Assert.Single(b.Build("x", "m", 64, 5000, "A", "B", 0, 0).Grenades);
        Assert.Equal(10f, g.ThrowTime);
        Assert.Equal(Side.T, g.ThrowerSide);
        Assert.False(g.Detonated);
    }

    [Fact]
    public void Flash_victims_are_classified_by_side()
    {
        var b = StartedRound();
        var id = b.ThrowGrenade(GrenadeType.Flashbang, 1, 700, Vector3.Zero, 0, 0, "", Vector3.Zero)!.Value;
        b.DetonateGrenade(id, 800, new Vector3(1, 2, 3));
        b.AddFlashVictim(id, 3, 2.0f);
        b.AddFlashVictim(id, 2, 1.0f);
        b.AddFlashVictim(id, 1, 0.5f);
        b.AddFlashVictim(id, 3, 0f); // нулевая длительность не считается

        var g = Assert.Single(b.Build("x", "m", 64, 5000, "A", "B", 0, 0).Grenades);
        Assert.Equal(new Vector3(1, 2, 3), g.DetonatePosition);
        Assert.Equal(3, g.FlashVictims.Count);
        Assert.False(g.FlashVictims.Single(v => v.SteamId == 3).IsTeammate);
        Assert.True(g.FlashVictims.Single(v => v.SteamId == 2).IsTeammate);
        Assert.True(g.FlashVictims.Single(v => v.SteamId == 1).IsSelf);
    }

    [Fact]
    public void Second_detonation_of_same_grenade_is_ignored()
    {
        var b = StartedRound();
        var id = b.ThrowGrenade(GrenadeType.HighExplosive, 1, 700, Vector3.Zero, 0, 0, "", Vector3.Zero)!.Value;
        b.DetonateGrenade(id, 800, new Vector3(1, 0, 0));
        b.DetonateGrenade(id, 900, new Vector3(2, 0, 0));

        Assert.Equal(new Vector3(1, 0, 0), b.Build("x", "m", 64, 5000, "A", "B", 0, 0).Grenades.Single().DetonatePosition);
    }

    [Fact]
    public void Grenades_of_restored_round_are_dropped()
    {
        var b = StartedRound(1);
        b.RoundEnd(2000, Side.T, RoundEndReason.TerroristsWin);
        b.RoundStart(2100);
        b.FreezeEnd(2200, 2, 1, 0, false, false, [new(1, "me", false, Side.T)]);
        b.ThrowGrenade(GrenadeType.Smoke, 1, 2300, Vector3.Zero, 0, 0, "", Vector3.Zero);
        b.RoundStart(2400);
        b.FreezeEnd(2500, 2, 1, 0, false, false, [new(1, "me", false, Side.T)]); // откат раунда 2

        Assert.Empty(b.Build("x", "m", 64, 5000, "A", "B", 0, 0).Grenades);
    }

    [Fact]
    public void Throw_before_any_round_is_ignored()
    {
        Assert.Null(new MatchBuilder().ThrowGrenade(GrenadeType.Smoke, 1, 10, Vector3.Zero, 0, 0, "", Vector3.Zero));
    }
}
