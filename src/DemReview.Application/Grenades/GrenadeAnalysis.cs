using System.Numerics;
using DemReview.Application.Damage;
using DemReview.Domain;

namespace DemReview.Application.Grenades;

/// <summary>Повторяющаяся раскидка: одинаковый тип, близкие точки броска и детонации.</summary>
/// <param name="ThrowPoint">Средняя точка броска.</param>
/// <param name="LandPoint">Средняя точка детонации.</param>
/// <param name="FromZone">Зона, откуда бросают.</param>
/// <param name="ToZone">Зона, куда прилетает граната (по ближайшим позициям игроков).</param>
public sealed record GrenadeCluster(
    int Id,
    GrenadeType Type,
    Side Side,
    Vector3 ThrowPoint,
    Vector3 LandPoint,
    string FromZone,
    string ToZone,
    IReadOnlyList<GrenadeThrow> Throws,
    float MedianThrowTime)
{
    public IReadOnlyList<int> Rounds => Throws.Select(t => t.Round).Distinct().Order().ToList();
}

/// <summary>Сводка по типу гранаты на стороне.</summary>
public sealed record GrenadeTypeSummary(GrenadeType Type, Side Side, int Count, int Rounds, float MedianThrowTime);

/// <summary>Флешки игрока: кого и на сколько ослепил.</summary>
public sealed record FlashSummary(
    int Thrown,
    int EnemiesBlinded,
    float EnemyBlindSeconds,
    int TeammatesBlinded,
    float TeammateBlindSeconds,
    int SelfBlinded,
    int WithoutEnemyVictims);

public sealed record GrenadeAnalysis(
    ulong SteamId,
    IReadOnlyList<GrenadeThrow> Throws,
    IReadOnlyList<GrenadeCluster> Clusters,
    IReadOnlyList<GrenadeTypeSummary> Summary,
    FlashSummary Flashes,
    IReadOnlyDictionary<int, string> LandZones,
    IReadOnlyDictionary<int, GrenadeDamage> Damage);

/// <summary>Параметры кластеризации раскидок (единицы — игровые units).</summary>
/// <param name="ThrowDistance">Максимальное расстояние между точками броска в одной раскидке.</param>
/// <param name="LandDistance">Максимальное расстояние между точками детонации в одной раскидке.</param>
public sealed record GrenadeClusterOptions(float ThrowDistance = 150, float LandDistance = 250);

public interface IGrenadeAnalysisService
{
    GrenadeAnalysis Analyze(MatchData match, ulong steamId, GrenadeClusterOptions? options = null);
}
