using DemReview.Application.Context;
using DemReview.Application.Review;
using DemReview.Domain;
using DemReview.Tests.Context;

namespace DemReview.Tests.Review;

public class SelfReviewTests
{
    private static IMatchContext Context(SyntheticMatch builder) => new RoundContextService().Create(builder.Build());

    [Fact]
    public void Death_without_a_trade_is_a_mistake_and_a_traded_one_is_not()
    {
        var context = Context(new SyntheticMatch().FiveVsFive().Rounds(2)
            .Kill(1, 10, attacker: 6, victim: 1)
            .Kill(2, 10, attacker: 6, victim: 1)
            .Kill(2, 13, attacker: 2, victim: 6));

        var deaths = MistakeAnalysis.Analyze(context, 1);

        var death = Assert.Single(deaths);
        Assert.Equal(1, death.Round);
        Assert.Equal([MistakeKind.Untraded], death.Mistakes);
        Assert.Equal(4, death.AliveAllies);
    }

    [Fact]
    public void Trade_after_the_window_does_not_count()
    {
        var context = Context(new SyntheticMatch().FiveVsFive().Rounds(1)
            .Kill(1, 10, attacker: 6, victim: 1)
            .Kill(1, 16, attacker: 2, victim: 6));

        Assert.True(Assert.Single(MistakeAnalysis.Analyze(context, 1)).Has(MistakeKind.Untraded));
    }

    [Fact]
    public void Last_player_alive_cannot_be_traded_or_isolated()
    {
        var context = Context(new SyntheticMatch().FiveVsFive().Rounds(1)
            .Route(1, 1, "A").ZonePosition("A", 4000, 0)
            .Kill(1, 5, 6, 2).Kill(1, 6, 6, 3).Kill(1, 7, 6, 4).Kill(1, 8, 6, 5)
            .Kill(1, 20, attacker: 7, victim: 1));

        Assert.Empty(MistakeAnalysis.Analyze(context, 1));
    }

    [Fact]
    public void Death_far_from_alive_teammates_is_isolated()
    {
        var context = Context(new SyntheticMatch().FiveVsFive().Rounds(1)
            .Route(1, 1, "A").ZonePosition("A", 2000, 0)
            .Kill(1, 10, attacker: 6, victim: 1)
            .Kill(1, 11, attacker: 2, victim: 6));

        var death = Assert.Single(MistakeAnalysis.Analyze(context, 1));
        Assert.Equal([MistakeKind.Isolated], death.Mistakes);
        Assert.Equal(2000, death.TeammateDistance!.Value, 1);
        Assert.Equal("A", death.Place);
    }

    [Fact]
    public void Unused_grenades_are_counted_with_their_price()
    {
        var context = Context(new SyntheticMatch().FiveVsFive().Rounds(1)
            .Kill(1, 10, attacker: 6, victim: 1, grenades: [GrenadeType.Flashbang, GrenadeType.Flashbang, GrenadeType.Smoke])
            .Kill(1, 11, attacker: 2, victim: 6));

        var death = Assert.Single(MistakeAnalysis.Analyze(context, 1));
        Assert.Equal([MistakeKind.UnusedGrenades], death.Mistakes);
        Assert.Equal(700, death.UnusedGrenadeValue);
    }

    [Fact]
    public void Death_on_the_same_spot_soon_after_a_won_duel_is_a_re_peek()
    {
        var context = Context(new SyntheticMatch().FiveVsFive().Rounds(2)
            .Kill(1, 10, attacker: 1, victim: 7)
            .Kill(1, 12, attacker: 6, victim: 1)
            .Kill(1, 13, attacker: 2, victim: 6)
            // Раунд 2: погиб через 6 с после убийства — это уже не повторный пик.
            .Kill(2, 10, attacker: 1, victim: 7)
            .Kill(2, 16, attacker: 6, victim: 1)
            .Kill(2, 17, attacker: 2, victim: 6));

        var death = Assert.Single(MistakeAnalysis.Analyze(context, 1));
        Assert.Equal(1, death.Round);
        Assert.Equal([MistakeKind.RePeek], death.Mistakes);
        Assert.Equal(2, death.RePeekAfter!.Value, 2);
        Assert.Equal(7UL, death.RePeekVictim);
    }

    [Fact]
    public void Clutch_starts_when_the_last_teammate_dies()
    {
        var context = Context(new SyntheticMatch().FiveVsFive().Rounds(1, _ => Side.T)
            .Route(1, 1, "Spawn", "Spawn", "Spawn", "Mid", "Mid", "Mid").ZonePosition("Mid", 500, 0)
            .Kill(1, 5, 6, 2).Kill(1, 6, 6, 3).Kill(1, 7, 6, 4).Kill(1, 8, 6, 5)
            .Kill(1, 20, attacker: 1, victim: 6)
            .Kill(1, 25, attacker: 1, victim: 7));

        var clutch = Assert.Single(KeySituationAnalysis.Analyze(context, 1));
        Assert.Equal(KeySituationKind.Clutch, clutch.Kind);
        Assert.Equal(0, clutch.Allies);
        Assert.Equal(5, clutch.Enemies);
        Assert.Equal(2, clutch.Kills);
        Assert.True(clutch.Won);
        Assert.True(clutch.Survived);
        Assert.Equal(8, clutch.Time, 1);
        Assert.Equal("Mid", clutch.Place);
    }

    [Fact]
    public void Planted_bomb_is_a_post_plant_for_T_and_a_retake_for_CT()
    {
        var context = Context(new SyntheticMatch().FiveVsFive().Rounds(1, _ => Side.CT)
            .Route(1, 1, "Spawn", "Spawn", "Spawn", "Spawn", "Spawn", "Spawn", "Site")
            .Bomb(1, 28, BombEventType.Planted)
            .Kill(1, 40, attacker: 6, victim: 1));

        var postPlant = Assert.Single(KeySituationAnalysis.Analyze(context, 1));
        Assert.Equal(KeySituationKind.PostPlant, postPlant.Kind);
        Assert.Equal(BombSite.A, postPlant.Site);
        Assert.Equal("Site", postPlant.Place);
        Assert.False(postPlant.Won);
        Assert.False(postPlant.Survived);
        Assert.Equal(4, postPlant.Allies);

        var retake = Assert.Single(KeySituationAnalysis.Analyze(context, 6));
        Assert.Equal(KeySituationKind.Retake, retake.Kind);
        Assert.True(retake.Won);
        Assert.Equal(1, retake.Kills);
    }

    [Fact]
    public void Player_dead_before_the_plant_has_no_post_plant()
    {
        var context = Context(new SyntheticMatch().FiveVsFive().Rounds(1)
            .Kill(1, 10, attacker: 6, victim: 1)
            .Bomb(1, 30, BombEventType.Planted));

        Assert.Empty(KeySituationAnalysis.Analyze(context, 1));
    }

    [Fact]
    public void Places_are_grouped_with_round_wins()
    {
        KeySituation At(string place, bool won) =>
            new(KeySituationKind.Retake, 1, 0, 0, Side.CT, 0, 0, won, false, 0, place, default, BombSite.A);

        var places = KeySituationAnalysis.Places([At("Ramp", true), At("Ramp", false), At("Ramp", true), At("Palace", false)]);

        Assert.Equal(["Ramp", "Palace"], places.Select(p => p.Place));
        Assert.Equal(3, places[0].Rounds);
        Assert.Equal(2, places[0].Wins);
    }

    [Fact]
    public void Duels_record_range_weapon_and_stance()
    {
        var match = new SyntheticMatch().FiveVsFive().Rounds(2)
            .Route(1, 1, "Mid").Route(6, 1, "Long").ZonePosition("Mid", 0, 0).ZonePosition("Long", 2000, 0)
            .Route(1, 2, "Mid", "Mid", "Short").Route(7, 2, "Short").ZonePosition("Short", 0, 300)
            .Kill(1, 10, attacker: 1, victim: 6)
            // Игрок перешёл из Mid в Short на 10-й секунде и сразу погиб — он выходил на угол.
            .Kill(2, 10.2f, attacker: 7, victim: 1, weapon: "awp")
            .Build();

        var duels = DuelAnalysis.Analyze(match, 1);

        Assert.Equal(2, duels.Count);
        var win = duels[0];
        Assert.True(win.Won);
        Assert.Equal(DuelRange.Long, win.Range);
        Assert.Equal(WeaponClass.Rifle, win.WeaponClass);
        Assert.Equal(DuelStance.Holding, win.Stance);

        var loss = duels[1];
        Assert.False(loss.Won);
        Assert.Equal(7UL, loss.OpponentSteamId);
        Assert.Equal("awp", loss.OpponentWeapon);
        Assert.Equal("ak47", loss.Weapon);
        Assert.Equal(DuelRange.Close, loss.Range);
        Assert.Equal(DuelStance.Peeking, loss.Stance);

        var byRange = DuelAnalysis.ByRange(duels);
        Assert.Equal([DuelRange.Close, DuelRange.Long], byRange.Select(r => r.Key));
        Assert.Equal((0, 1), (byRange[0].Wins, byRange[0].Losses));
        Assert.Equal(0.5, DuelAnalysis.ByWeaponClass(duels).Single().WinRate);
    }

    [Fact]
    public void Grenade_and_team_kills_are_not_duels()
    {
        var match = new SyntheticMatch().FiveVsFive().Rounds(1)
            .Kill(1, 10, attacker: 1, victim: 6, weapon: "hegrenade")
            .Kill(1, 11, attacker: 1, victim: 2)
            .Build();

        Assert.Empty(DuelAnalysis.Analyze(match, 1));
    }

    [Fact]
    public void Self_review_reports_data_from_old_parses()
    {
        var match = new SyntheticMatch().FiveVsFive().Rounds(1).Kill(1, 10, 6, 1).Build();
        var old = match with { Kills = match.Kills.Select(k => k with { VictimGrenades = null }).ToList() };

        var review = SelfReview.Analyze(new RoundContextService().Create(old), 1);

        Assert.Equal(1, review.Deaths);
        Assert.False(review.HasGrenadeData);
        Assert.False(review.HasDemoTicks);
        Assert.Equal(1, review.Count(MistakeKind.Untraded));
    }

    [Fact]
    public void Demo_command_goes_a_few_seconds_before_the_moment()
    {
        var match = new SyntheticMatch().FiveVsFive().Rounds(1).Build();
        var round = match.Rounds[0];
        var withTicks = match with { Rounds = [round with { DemoTickOffset = 100 }] };
        var tick = SyntheticMatch.At(1, 30);

        Assert.Null(DemoCommands.GoToTick(match, 1, tick, 5));
        Assert.Equal($"demo_gototick {tick - 5 * 64 - 100}", DemoCommands.GoToTick(withTicks, 1, tick, 5));
        // Не раньше начала раунда (тик демки не меньше нуля).
        Assert.Equal("demo_gototick 0", DemoCommands.GoToTick(withTicks, 1, round.StartTick + 10 + 100, 5));
    }
}
