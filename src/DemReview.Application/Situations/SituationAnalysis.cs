using DemReview.Application.Context;
using DemReview.Domain;

namespace DemReview.Application.Situations;

public enum ScoreState
{
    Tied,
    Leading,
    Trailing,
}

public enum Aggression
{
    Aggressive,
    Neutral,
    Passive,
}

public enum FirstDuel
{
    NotInvolved,
    Won,
    Lost,
}

/// <summary>Ситуация раунда (условия) и решение игрока (действия).</summary>
/// <param name="RouteKey">Первые зоны маршрута, например «TSpawn → Ramp → Palace».</param>
/// <param name="FinalZone">Последняя зона, где был игрок (место смерти или позиция в конце раунда).</param>
/// <param name="FirstContactTime">Секунды до первого урона/убийства с участием игрока; null — контакта не было.</param>
/// <param name="MatchupAtFirstContact">Численность в момент первого контакта, например «5v4».</param>
public sealed record RoundSituation(
    int Round,
    Side Side,
    // Условия
    BuyType PlayerBuy,
    BuyType TeamBuy,
    BuyType EnemyBuy,
    int PlayerEquipment,
    int TeamAverageEquipment,
    int ScoreUs,
    int ScoreThem,
    ScoreState Score,
    bool IsOvertime,
    // Решение
    string RouteKey,
    string FinalZone,
    IReadOnlyDictionary<GrenadeType, int> Grenades,
    float? FirstContactTime,
    string MatchupAtFirstContact,
    Aggression Aggression,
    FirstDuel FirstDuel,
    // Итог
    int Kills,
    bool Died,
    bool RoundWon)
{
    public int GrenadeCount => Grenades.Values.Sum();
}

/// <summary>Строка частотной таблицы: при условии X решение Y встречалось Count раз из Total.</summary>
public sealed record FrequencyRow(string Condition, string Decision, int Count, int Total, IReadOnlyList<int> Rounds)
{
    public double Share => Total == 0 ? 0 : (double)Count / Total;
}

public sealed record FrequencyTable(string Name, string ConditionTitle, string DecisionTitle, IReadOnlyList<FrequencyRow> Rows);

public sealed record SituationAnalysis(ulong SteamId, IReadOnlyList<RoundSituation> Rounds, IReadOnlyList<FrequencyTable> Tables);

/// <summary>Параметры анализа ситуаций.</summary>
public sealed record SituationOptions(
    EconomyOptions? Economy = null,
    float AggressiveContactSeconds = 20,
    float PassiveContactSeconds = 50,
    int RoutePrefixLength = 3);

public interface ISituationAnalysisService
{
    SituationAnalysis Analyze(MatchData match, ulong steamId, SituationOptions? options = null);
}
