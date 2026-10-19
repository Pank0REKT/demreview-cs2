using DemReview.Application.Context;
using DemReview.Application.Routes;
using DemReview.Application.Situations;
using DemReview.Domain;

namespace DemReview.Tests.Context;

public class SituationAnalysisTests
{
    private readonly SituationAnalysisService _service = new(new RoundContextService(), new RouteAnalysisService());

    [Fact]
    public void Early_contact_is_aggressive_late_or_none_is_passive()
    {
        var match = new SyntheticMatch().FiveVsFive().Rounds(3)
            .Damage(1, 12, attacker: 1, victim: 6)
            .Damage(2, 35, attacker: 7, victim: 1)
            .Damage(3, 55, attacker: 1, victim: 8)
            .Build();

        var rounds = _service.Analyze(match, 1).Rounds;

        Assert.Equal(Aggression.Aggressive, rounds[0].Aggression);
        Assert.Equal(12f, rounds[0].FirstContactTime!.Value, 0.1f);
        Assert.Equal(Aggression.Neutral, rounds[1].Aggression);
        Assert.Equal(Aggression.Passive, rounds[2].Aggression);
    }

    [Fact]
    public void No_contact_is_passive()
    {
        var match = new SyntheticMatch().FiveVsFive().Rounds(1).Build();

        var round = Assert.Single(_service.Analyze(match, 1).Rounds);
        Assert.Null(round.FirstContactTime);
        Assert.Equal(Aggression.Passive, round.Aggression);
    }

    [Fact]
    public void Aggression_thresholds_are_configurable()
    {
        var match = new SyntheticMatch().FiveVsFive().Rounds(1).Damage(1, 30, 1, 6).Build();

        var round = _service.Analyze(match, 1, new SituationOptions(AggressiveContactSeconds: 40)).Rounds.Single();
        Assert.Equal(Aggression.Aggressive, round.Aggression);
    }

    [Fact]
    public void First_duel_result_is_detected()
    {
        var match = new SyntheticMatch().FiveVsFive().Rounds(3)
            .Kill(1, 10, attacker: 1, victim: 6)
            .Kill(2, 10, attacker: 6, victim: 1)
            .Kill(3, 10, attacker: 2, victim: 7)
            .Kill(3, 20, attacker: 1, victim: 8)
            .Build();

        var rounds = _service.Analyze(match, 1).Rounds;

        Assert.Equal(FirstDuel.Won, rounds[0].FirstDuel);
        Assert.Equal(FirstDuel.Lost, rounds[1].FirstDuel);
        Assert.True(rounds[1].Died);
        Assert.Equal(FirstDuel.NotInvolved, rounds[2].FirstDuel);
        Assert.Equal(1, rounds[2].Kills);
    }

    [Fact]
    public void Matchup_at_first_contact_reflects_earlier_deaths()
    {
        var match = new SyntheticMatch().FiveVsFive().Rounds(1)
            .Kill(1, 5, attacker: 6, victim: 2)
            .Damage(1, 15, attacker: 1, victim: 7)
            .Build();

        Assert.Equal("4v5", _service.Analyze(match, 1).Rounds.Single().MatchupAtFirstContact);
    }

    [Fact]
    public void Route_final_zone_and_grenades_are_collected()
    {
        var match = new SyntheticMatch().FiveVsFive().Rounds(1)
            .Route(1, 1, "TSpawn", "Ramp", "Palace", "BombsiteA")
            .Grenade(1, 1, GrenadeType.Smoke)
            .Grenade(1, 1, GrenadeType.Flashbang)
            .Build();

        var round = _service.Analyze(match, 1).Rounds.Single();

        Assert.Equal("TSpawn → Ramp → Palace", round.RouteKey);
        Assert.Equal("BombsiteA", round.FinalZone);
        Assert.Equal(2, round.GrenadeCount);
        Assert.Equal(1, round.Grenades[GrenadeType.Smoke]);
    }

    [Fact]
    public void Frequency_table_counts_decision_share_per_condition()
    {
        var match = new SyntheticMatch().FiveVsFive().Rounds(4)
            .Route(1, 1, "TSpawn", "Ramp", "Palace").Route(1, 2, "TSpawn", "Ramp", "Palace")
            .Route(1, 3, "TSpawn", "Ramp", "Palace").Route(1, 4, "TSpawn", "Mid", "Window")
            .Build();

        var table = _service.Analyze(match, 1).Tables.Single(t => t.Name == "Закупка → маршрут");
        var rows = table.Rows.Where(r => r.Condition == "T · фулл-бай").ToList();

        var top = rows[0];
        Assert.Equal("TSpawn → Ramp → Palace", top.Decision);
        Assert.Equal(2, top.Count);          // раунд 1 — пистолетка, отдельное условие
        Assert.Equal(3, top.Total);
        Assert.Equal([2, 3], top.Rounds);
        Assert.Contains(table.Rows, r => r.Condition == "T · пистолетка" && r.Count == 1);
    }

    [Fact]
    public void Score_state_is_from_player_side()
    {
        var match = new SyntheticMatch().FiveVsFive().Rounds(3, winner: _ => Side.T).Build();

        var rounds = _service.Analyze(match, 1).Rounds;
        Assert.Equal(ScoreState.Tied, rounds[0].Score);
        Assert.Equal(ScoreState.Leading, rounds[2].Score);
        Assert.Equal(ScoreState.Trailing, _service.Analyze(match, 6).Rounds[2].Score);
    }
}
