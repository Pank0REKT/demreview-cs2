using System.Numerics;
using DemReview.Domain;
using DemReview.Infrastructure.Parsing;

namespace DemReview.Tests.Parsing;

public class MatchBuilderTests
{
    private const ulong Alice = 1, Bob = 2, Carol = 3;

    private static PlayerSnapshot[] Lineup(Side aliceSide = Side.T) =>
    [
        new(Alice, "alice", false, aliceSide),
        new(Bob, "bob", false, aliceSide.Opposite()),
        new(Carol, "carol", false, aliceSide),
    ];

    private static void PlayRound(MatchBuilder b, int number, int startTick, Side winner, Side aliceSide = Side.T,
        int scoreT = 0, int scoreCT = 0)
    {
        b.RoundStart(startTick);
        b.FreezeEnd(startTick + 100, number, scoreT, scoreCT, isOvertime: false, isWarmup: false, Lineup(aliceSide));
        b.RoundEnd(startTick + 1000, winner, winner == Side.T ? RoundEndReason.TerroristsWin : RoundEndReason.CTsWin);
    }

    private static MatchData Build(MatchBuilder b) => b.Build("x.dem", "de_mirage", 64, 100_000, "A", "B", 0, 0);

    [Fact]
    public void Demo_tick_offset_and_victim_grenades_are_kept()
    {
        var b = new MatchBuilder();
        b.RoundStart(5000);
        b.FreezeEnd(5100, 1, 0, 0, isOvertime: false, isWarmup: false, Lineup(), demoTickOffset: 4000);
        b.Kill(5500, Alice, Bob, null, "ak47", false, null, Vector3.Zero, [GrenadeType.Smoke, GrenadeType.Flashbang]);
        b.RoundEnd(6000, Side.T, RoundEndReason.TerroristsWin);

        var match = Build(b);

        Assert.Equal(1500, match.Rounds[0].DemoTickOf(5500));
        Assert.Equal([GrenadeType.Smoke, GrenadeType.Flashbang], match.Kills[0].VictimGrenades);
    }

    [Fact]
    public void Rounds_before_begin_new_match_are_discarded()
    {
        var b = new MatchBuilder();
        PlayRound(b, 1, 0, Side.T);           // предматчевый раунд
        b.Kill(500, Alice, Bob, null, "ak47", false, null, Vector3.Zero);
        b.BeginNewMatch();
        PlayRound(b, 1, 5000, Side.CT);
        PlayRound(b, 2, 7000, Side.T);

        var match = Build(b);

        Assert.Equal([1, 2], match.Rounds.Select(r => r.Number));
        Assert.Equal(Side.CT, match.Rounds[0].Winner);
        Assert.Empty(match.Kills);
    }

    [Fact]
    public void Warmup_freeze_end_does_not_open_round()
    {
        var b = new MatchBuilder();
        b.FreezeEnd(100, 1, 0, 0, false, isWarmup: true, Lineup());
        b.RoundEnd(200, Side.T, RoundEndReason.TerroristsWin);

        Assert.Empty(Build(b).Rounds);
    }

    [Fact]
    public void Round_restore_overwrites_later_rounds()
    {
        var b = new MatchBuilder();
        PlayRound(b, 1, 0, Side.T);
        PlayRound(b, 2, 2000, Side.T);
        b.Kill(2500, Alice, Bob, null, "ak47", false, null, Vector3.Zero);
        PlayRound(b, 2, 4000, Side.CT);       // откат и переигровка раунда 2

        var match = Build(b);

        Assert.Equal(2, match.Rounds.Count);
        Assert.Equal(Side.CT, match.Rounds[1].Winner);
        Assert.Empty(match.Kills);
    }

    [Fact]
    public void Unfinished_last_round_is_kept_with_unknown_winner()
    {
        var b = new MatchBuilder();
        PlayRound(b, 1, 0, Side.T);
        b.RoundStart(2000);
        b.FreezeEnd(2100, 2, 1, 0, false, false, Lineup());

        var match = Build(b);

        Assert.Equal(2, match.Rounds.Count);
        Assert.Equal(Side.None, match.Rounds[1].Winner);
        Assert.Equal(RoundEndReason.Unknown, match.Rounds[1].EndReason);
    }

    [Fact]
    public void Side_is_tracked_per_round_across_halftime()
    {
        var b = new MatchBuilder();
        PlayRound(b, 1, 0, Side.T, aliceSide: Side.T);
        PlayRound(b, 2, 2000, Side.CT, aliceSide: Side.CT);

        var alice = Build(b).FindPlayer(Alice)!;

        Assert.Equal(Side.T, alice.SideInRound(1));
        Assert.Equal(Side.CT, alice.SideInRound(2));
        Assert.Equal(Side.CT, alice.FinalSide);
        Assert.Equal("B", alice.TeamName);
    }

    [Fact]
    public void Kda_counts_kills_deaths_assists_and_penalizes_team_kills()
    {
        var b = new MatchBuilder();
        b.RoundStart(0);
        b.FreezeEnd(100, 1, 0, 0, false, false, Lineup());
        b.Kill(200, Alice, Bob, Carol, "ak47", true, Vector3.One, Vector3.Zero);   // Alice → Bob, ассист Carol
        b.Kill(300, Alice, Carol, null, "ak47", false, Vector3.One, Vector3.Zero); // тимкилл
        b.Kill(400, null, Alice, null, "world", false, null, Vector3.Zero);        // смерть от мира
        b.RoundEnd(1000, Side.CT, RoundEndReason.CTsWin);

        var match = Build(b);
        var alice = match.FindPlayer(Alice)!;
        var carol = match.FindPlayer(Carol)!;

        Assert.Equal((0, 1, 0), (alice.Kills, alice.Deaths, alice.Assists));
        Assert.Equal((0, 1, 1), (carol.Kills, carol.Deaths, carol.Assists));
        Assert.Equal(1, match.FindPlayer(Bob)!.Deaths);
    }

    [Fact]
    public void Kills_after_round_end_belong_to_that_round()
    {
        var b = new MatchBuilder();
        PlayRound(b, 1, 0, Side.T);
        b.Kill(1100, Alice, Bob, null, "ak47", false, null, Vector3.Zero);

        Assert.Equal(1, Build(b).Kills.Single().Round);
    }

    [Fact]
    public void Reconnected_player_keeps_single_entry_and_latest_name()
    {
        var b = new MatchBuilder();
        PlayRound(b, 1, 0, Side.T);
        // Переподключился с новым ником: тот же SteamID.
        b.ObservePlayer(new PlayerSnapshot(Alice, "alice_renamed", false, Side.T));
        b.RoundStart(2000);
        b.FreezeEnd(2100, 2, 1, 0, false, false,
            [new(Alice, "alice_renamed", false, Side.T), new(Bob, "bob", false, Side.CT)]);
        b.RoundEnd(3000, Side.T, RoundEndReason.TerroristsWin);

        var match = Build(b);

        Assert.Single(match.Players, p => p.SteamId == Alice);
        Assert.Equal("alice_renamed", match.FindPlayer(Alice)!.Name);
    }

    [Fact]
    public void Players_without_rounds_are_excluded()
    {
        var b = new MatchBuilder();
        b.ObservePlayer(new PlayerSnapshot(99, "spectator_like", false, Side.T));
        PlayRound(b, 1, 0, Side.T);

        Assert.Null(Build(b).FindPlayer(99));
    }

    [Fact]
    public void Bot_key_is_stable_and_marked()
    {
        var key = DemoFileDemoParser.BotKey("Bot Alex");

        Assert.Equal(key, DemoFileDemoParser.BotKey("Bot Alex"));
        Assert.NotEqual(key, DemoFileDemoParser.BotKey("Bot Bob"));
        Assert.True((key & 0x8000_0000_0000_0000UL) != 0);
    }
}
