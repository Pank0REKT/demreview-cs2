using DemReview.Application.Context;
using DemReview.Domain;

namespace DemReview.Tests.Context;

public class RoundContextTests
{
    private readonly RoundContextService _service = new();

    [Fact]
    public void Pistol_rounds_are_first_round_and_first_after_side_switch()
    {
        var match = new SyntheticMatch().FiveVsFive(halfAfter: 3).Rounds(6).Build();
        var context = _service.Create(match);

        var pistols = match.Rounds.Where(r => context.Economy(r.Number)!.IsPistol).Select(r => r.Number);

        Assert.Equal([1, 4], pistols);
        Assert.Equal(BuyType.Pistol, context.Economy(4)!.T.Buy);
    }

    [Theory]
    [InlineData(800, BuyType.Eco)]
    [InlineData(2500, BuyType.Force)]
    [InlineData(4800, BuyType.FullBuy)]
    public void Team_buy_is_classified_by_average_equipment(int equipment, BuyType expected)
    {
        var match = new SyntheticMatch().FiveVsFive().Rounds(2).Equipment(2, _ => equipment).Build();

        Assert.Equal(expected, _service.Create(match).Economy(2)!.T.Buy);
    }

    [Fact]
    public void Buy_thresholds_are_configurable()
    {
        var match = new SyntheticMatch().FiveVsFive().Rounds(2).Equipment(2, _ => 2500).Build();
        var options = new EconomyOptions(EcoMaxEquipment: 3000, FullMinEquipment: 4000);

        Assert.Equal(BuyType.Eco, _service.Create(match, options).Economy(2)!.T.Buy);
    }

    [Fact]
    public void Player_buy_differs_from_team_buy()
    {
        var match = new SyntheticMatch().FiveVsFive().Rounds(2).Equipment(2, id => id == 1 ? 900 : 5000).Build();
        var economy = _service.Create(match).Economy(2)!;

        Assert.Equal(BuyType.Eco, economy.PlayerBuy[1]);
        Assert.Equal(BuyType.FullBuy, economy.PlayerBuy[2]);
    }

    [Fact]
    public void Alive_counts_follow_kills()
    {
        var match = new SyntheticMatch().FiveVsFive().Rounds(1)
            .Kill(1, 10, attacker: 6, victim: 1)
            .Kill(1, 20, attacker: 2, victim: 7)
            .Kill(1, 30, attacker: 6, victim: 3)
            .Build();
        var context = _service.Create(match);

        var moment = context.At(2, 1, SyntheticMatch.At(1, 35));

        Assert.Equal(2, moment.AliveAllies);      // T: 5 − 2 погибших − сам игрок
        Assert.Equal(4, moment.AliveEnemies);
        Assert.Equal("3v4", moment.Matchup);
        Assert.Equal("5v5", context.At(2, 1, SyntheticMatch.At(1, 5)).Matchup);
    }

    [Fact]
    public void Dead_player_is_not_counted_himself()
    {
        var match = new SyntheticMatch().FiveVsFive().Rounds(1).Kill(1, 10, 6, 1).Build();
        var moment = _service.Create(match).At(1, 1, SyntheticMatch.At(1, 15));

        Assert.False(moment.PlayerAlive);
        Assert.Equal(4, moment.AliveAllies);
        Assert.Equal("4v5", moment.Matchup);
    }

    [Fact]
    public void Bomb_state_changes_over_time()
    {
        var match = new SyntheticMatch().FiveVsFive().Rounds(1)
            .Bomb(1, 40, BombEventType.Planted, BombSite.B)
            .Bomb(1, 70, BombEventType.Defused, BombSite.B)
            .Build();
        var context = _service.Create(match);

        Assert.Equal(BombState.NotPlanted, context.BombAt(1, SyntheticMatch.At(1, 30)).State);
        Assert.Equal((BombState.Planted, BombSite.B), context.BombAt(1, SyntheticMatch.At(1, 50)));
        Assert.Equal(BombState.Defused, context.BombAt(1, SyntheticMatch.At(1, 75)).State);
    }

    [Fact]
    public void Score_is_from_player_perspective()
    {
        var match = new SyntheticMatch().FiveVsFive().Rounds(3, winner: r => r == 1 ? Side.T : Side.CT).Build();
        var context = _service.Create(match);

        var ct = context.At(6, 3, SyntheticMatch.At(3, 1));
        Assert.Equal((1, 1), (ct.ScoreUs, ct.ScoreThem));
        var t = context.At(1, 2, SyntheticMatch.At(2, 1));
        Assert.Equal((1, 0), (t.ScoreUs, t.ScoreThem));
    }
}
