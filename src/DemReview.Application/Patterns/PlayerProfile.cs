using DemReview.Application.Context;
using DemReview.Domain;

namespace DemReview.Application.Patterns;

public enum PatternCategory
{
    Route,
    Position,
    Timing,
    Aggression,
    Rotation,
    Reaction,
    Grenades,
    Deaths,
}

public enum Confidence
{
    Low,
    Medium,
    High,
}

/// <summary>
/// Устойчивый паттерн поведения: условие + действие + частота + раунды-примеры.
/// </summary>
/// <param name="Text">Формулировка на русском, например «На CT в 4 из 5 раундов …».</param>
/// <param name="Count">В скольких случаях действие совершено.</param>
/// <param name="Total">Сколько раз выполнялось условие.</param>
/// <param name="ConfidenceScore">Нижняя граница 95% интервала Уилсона для доли.</param>
/// <param name="Rounds">Раунды-примеры (где действие совершено).</param>
/// <param name="GrenadeIds">Гранаты для подсветки на радаре (для раскидок и флешек).</param>
public sealed record Pattern(
    PatternCategory Category,
    Side Side,
    string Condition,
    string Action,
    string Text,
    int Count,
    int Total,
    double ConfidenceScore,
    Confidence Confidence,
    IReadOnlyList<int> Rounds,
    IReadOnlyList<int> GrenadeIds)
{
    public double Share => Total == 0 ? 0 : (double)Count / Total;
}

public enum PlayerRole
{
    Entry,
    Lurker,
    Support,
    Anchor,
    Rotator,
    Rifler,
}

/// <summary>Роль на стороне с обоснованием по метрикам.</summary>
public sealed record RoleAssessment(Side Side, PlayerRole Role, Confidence Confidence, IReadOnlyList<string> Reasons);

/// <summary>Метрики игрока на стороне (используются для роли и сводки).</summary>
/// <param name="MedianContactTime">Медиана времени первого контакта, секунды (только раунды с контактом).</param>
/// <param name="FirstDuelShare">Доля раундов, где игрок участвовал в первой дуэли.</param>
/// <param name="MedianTeammateDistance">Медианное расстояние до ближайшего живого союзника на 10–40 с раунда, units.</param>
/// <param name="DamagePerRound">Средний фактический урон по противникам за раунд (ADR).</param>
/// <param name="AnchorShare">Доля раундов, где игрок большую часть первых 40 с держал одну зону (не спавн).</param>
public sealed record SideMetrics(
    Side Side,
    int Rounds,
    int Kills,
    int Deaths,
    double SurvivalShare,
    double RoundWinShare,
    float? MedianContactTime,
    double ContactShare,
    double FirstDuelShare,
    double FirstDuelWinShare,
    float MedianTeammateDistance,
    double GrenadesPerRound,
    double DamagePerRound,
    double AnchorShare,
    double AggressiveShare,
    double PassiveShare);

public sealed record PlayerProfile(
    ulong SteamId,
    string Name,
    string MapName,
    IReadOnlyList<RoleAssessment> Roles,
    IReadOnlyList<SideMetrics> Sides,
    IReadOnlyList<Pattern> Patterns,
    int PredictabilityPercent,
    int RoundsAnalyzed,
    PatternOptions Options);

/// <summary>Пороги выявления паттернов.</summary>
/// <param name="MinShare">Минимальная частота (по умолчанию 60%).</param>
/// <param name="MinExamples">Минимум раундов-примеров (по умолчанию 3).</param>
public sealed record PatternOptions(
    double MinShare = 0.6,
    int MinExamples = 3,
    EconomyOptions? Economy = null,
    float AggressiveContactSeconds = 20,
    float PassiveContactSeconds = 50,
    float GrenadeClusterThrowDistance = 150,
    float GrenadeClusterLandDistance = 250);

public interface IPlayerPatternService
{
    PlayerProfile BuildProfile(MatchData match, ulong steamId, PatternOptions? options = null);
}

public static class PatternStats
{
    /// <summary>Нижняя граница интервала Уилсона (z = 1,96): учитывает малую выборку.</summary>
    public static double WilsonLowerBound(int successes, int total, double z = 1.96)
    {
        if (total == 0)
            return 0;
        var p = (double)successes / total;
        var z2 = z * z;
        var center = p + z2 / (2 * total);
        var margin = z * Math.Sqrt(p * (1 - p) / total + z2 / (4.0 * total * total));
        return Math.Max(0, (center - margin) / (1 + z2 / total));
    }

    public static Confidence Level(double wilson) => wilson switch
    {
        >= 0.5 => Confidence.High,
        >= 0.3 => Confidence.Medium,
        _ => Confidence.Low,
    };

    public static float Median(IEnumerable<float> values)
    {
        var sorted = values.Order().ToList();
        if (sorted.Count == 0)
            return 0;
        return sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
    }
}
