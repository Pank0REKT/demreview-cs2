using System.Globalization;
using System.Numerics;
using DemReview.Application.Lineups;
using DemReview.Domain;
using DemReview.Infrastructure.Parsing;
using DemReview.Infrastructure.Storage;
using DemReview.Tests.Context;

namespace DemReview.Tests.Lineups;

public class LineupTests
{
    private static GrenadeThrow Throw(int round = 1, string place = "TSpawn", float pitch = -12.345f, float yaw = 95.5f) =>
        new(7, GrenadeType.Smoke, 1, Side.T, round, 5000, 5, new Vector3(-1234.5678f, 345.6f, -167.96875f), pitch, yaw, place,
            Vector3.Zero, 5200, new Vector3(100, 200, 0), [])
        {
            ThrowerMovement = MovementFlags.Airborne | MovementFlags.Ducking,
            ThrowerSpeed = 3,
        };

    private static Lineup Lineup(GrenadeThrow? g = null) =>
        LineupFactory.FromThrow(new SyntheticMatch().FiveVsFive().Rounds(3).Build(), g ?? Throw(), "Window", DateTime.UtcNow);

    [Fact]
    public void SetPos_uses_invariant_culture_and_getpos_format()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("ru-RU");
        try
        {
            Assert.Equal("setpos -1234.57 345.60 -167.97;setang -12.35 95.50 0", LineupCommands.SetPos(Lineup()));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData(350f, -10f)]
    [InlineData(10f, 10f)]
    [InlineData(-30f, -30f)]
    [InlineData(275f, -85f)]
    [InlineData(95f, 89f)]
    public void Pitch_is_normalized_to_the_console_range(float pitch, float expected) =>
        Assert.Equal(expected, LineupCommands.NormalizePitch(pitch), 3);

    [Theory]
    [InlineData(270f, -90f)]
    [InlineData(-190f, 170f)]
    [InlineData(540f, 180f)]
    [InlineData(45f, 45f)]
    public void Yaw_is_normalized_to_plus_minus_180(float yaw, float expected) =>
        Assert.Equal(expected, LineupCommands.NormalizeYaw(yaw), 3);

    [Fact]
    public void Lineup_from_throw_keeps_position_angles_and_technique()
    {
        var lineup = Lineup();

        Assert.Equal("de_mirage", lineup.MapName);
        Assert.Equal(GrenadeType.Smoke, lineup.Type);
        Assert.Equal("TSpawn → Window", lineup.Title);
        Assert.Equal("x.dem", lineup.SourceFile);
        Assert.Equal(1, lineup.Round);
        Assert.Equal("p1", lineup.ThrowerName);
        Assert.Equal(MovementFlags.Airborne | MovementFlags.Ducking, lineup.Movement);
        Assert.Equal(new Vector3(100, 200, 0), lineup.LandPosition);
    }

    [Fact]
    public void Lineup_from_combined_match_points_to_the_source_demo_and_its_round()
    {
        var match = new SyntheticMatch().FiveVsFive().Rounds(30).Build() with
        {
            Parts =
            [
                new MatchPart("C:/demos/first.dem", new MatchScore("A", 13, "B", 7), 1, 20, 0, DateTime.UtcNow),
                new MatchPart("C:/demos/second.dem", new MatchScore("A", 5, "B", 5), 21, 30, 20, DateTime.UtcNow),
            ],
        };

        var lineup = LineupFactory.FromThrow(match, Throw(round: 23), "", DateTime.UtcNow);

        Assert.Equal("second.dem", lineup.SourceFile);
        Assert.Equal(3, lineup.Round);
        Assert.Equal("TSpawn → ?", lineup.Title);
    }

    [Fact]
    public void Same_throw_is_detected_despite_angle_wraparound()
    {
        var a = Lineup(Throw(pitch: 350, yaw: 179.99f));
        var b = Lineup(Throw(pitch: -10, yaw: -180f));
        var c = Lineup(Throw(pitch: -10, yaw: 90));

        Assert.True(a.IsSameThrow(b));
        Assert.False(a.IsSameThrow(c));
    }

    [Fact]
    public void Throw_movement_reaches_the_match_and_survives_the_cache()
    {
        var b = new MatchBuilder();
        b.RoundStart(0);
        b.FreezeEnd(100, 1, 0, 0, isOvertime: false, isWarmup: false, [new PlayerSnapshot(1, "alice", false, Side.T)]);
        b.ThrowGrenade(GrenadeType.Flashbang, 1, 500, Vector3.One, -5, 90, "Palace", Vector3.One, MovementFlags.Airborne, 215);
        b.RoundEnd(1000, Side.T, RoundEndReason.TerroristsWin);
        var match = b.Build("x.dem", "de_mirage", 64, 100_000, "A", "B", 0, 0);

        var grenade = Assert.Single(SqliteMatchCache.Deserialize(SqliteMatchCache.Serialize(match)).Grenades);

        Assert.Equal(MovementFlags.Airborne, grenade.ThrowerMovement);
        Assert.Equal(215f, grenade.ThrowerSpeed);
    }
}
