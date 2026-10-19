using DemReview.Domain;

namespace DemReview.Application.Context;

/// <summary>Пороги определения типа закупки.</summary>
/// <param name="EcoMaxEquipment">Средняя экипировка ниже — эко.</param>
/// <param name="FullMinEquipment">Средняя экипировка не ниже — фулл-бай; между порогами — форс.</param>
/// <param name="BuyWindowSeconds">Экипировка берётся как максимум за первые N секунд раунда (время закупки).</param>
public sealed record EconomyOptions(int EcoMaxEquipment = 1500, int FullMinEquipment = 3700, float BuyWindowSeconds = 20);

/// <summary>Экономика стороны в раунде.</summary>
public sealed record TeamEconomy(Side Side, BuyType Buy, int AverageEquipment, int TotalMoney, int Players);

/// <summary>Экономика раунда: закупка обеих сторон и каждого игрока.</summary>
public sealed record RoundEconomy(
    int Round,
    bool IsPistol,
    TeamEconomy T,
    TeamEconomy CT,
    IReadOnlyDictionary<ulong, int> PlayerEquipment,
    IReadOnlyDictionary<ulong, int> PlayerMoney,
    IReadOnlyDictionary<ulong, BuyType> PlayerBuy)
{
    public TeamEconomy Of(Side side) => side == Side.CT ? CT : T;
}

public enum BombState
{
    NotPlanted,
    Planted,
    Defused,
    Exploded,
}

/// <summary>Контекст момента для игрока: сторона, экономика, живые, бомба, счёт.</summary>
/// <param name="AliveAllies">Живые союзники, не считая самого игрока.</param>
public sealed record MomentContext(
    int Round,
    int Tick,
    float TimeInRound,
    Side Side,
    bool PlayerAlive,
    int Money,
    int Equipment,
    BuyType PlayerBuy,
    BuyType TeamBuy,
    BuyType EnemyBuy,
    int TeamAverageEquipment,
    int AliveAllies,
    int AliveEnemies,
    BombState Bomb,
    BombSite BombSite,
    int ScoreUs,
    int ScoreThem)
{
    /// <summary>Численное соотношение с точки зрения игрока, например «4v5».</summary>
    public string Matchup => $"{(PlayerAlive ? AliveAllies + 1 : AliveAllies)}v{AliveEnemies}";
}

/// <summary>Предрасчитанный контекст матча: экономика раундов, хронология смертей и бомбы.</summary>
public interface IMatchContext
{
    MatchData Match { get; }

    RoundEconomy? Economy(int round);

    /// <summary>Контекст в тике <paramref name="tick"/> раунда <paramref name="round"/>.</summary>
    MomentContext At(ulong steamId, int round, int tick);

    /// <summary>Живые игроки стороны в тике (по убийствам с начала раунда).</summary>
    int AliveCount(int round, Side side, int tick);

    bool IsAlive(ulong steamId, int round, int tick);

    (BombState State, BombSite Site) BombAt(int round, int tick);
}

public interface IRoundContextService
{
    IMatchContext Create(MatchData match, EconomyOptions? options = null);
}
