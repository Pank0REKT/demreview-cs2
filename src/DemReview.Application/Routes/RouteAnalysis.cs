using DemReview.Domain;

namespace DemReview.Application.Routes;

/// <summary>Посещение зоны карты в раунде.</summary>
/// <param name="Arrival">Время прихода, секунды от конца фризтайма.</param>
/// <param name="Leave">Время ухода (последний сэмпл в зоне).</param>
public sealed record ZoneVisit(string Zone, float Arrival, float Leave)
{
    public float Duration => Leave - Arrival;
}

/// <summary>Маршрут игрока в одном раунде.</summary>
/// <param name="Path">Сэмплы позиций игрока в раунде (для отрисовки).</param>
/// <param name="Died">Игрок погиб в этом раунде.</param>
public sealed record RoundRoute(int Round, Side Side, IReadOnlyList<ZoneVisit> Zones, IReadOnlyList<PlayerSample> Path, bool Died)
{
    public IEnumerable<string> ZoneNames => Zones.Select(z => z.Zone);
}

/// <summary>Повторяющийся маршрут: одинаковая последовательность первых зон.</summary>
/// <param name="Share">Доля раундов этой стороны, в которых игрок шёл так.</param>
public sealed record FrequentRoute(Side Side, IReadOnlyList<string> Zones, IReadOnlyList<int> Rounds, double Share);

/// <summary>Время прихода в зону (ключевую точку) по раундам.</summary>
public sealed record ZoneTiming(Side Side, string Zone, int Rounds, float MedianArrival, float EarliestArrival, float LatestArrival);

public sealed record RouteAnalysis(
    ulong SteamId,
    IReadOnlyList<RoundRoute> Rounds,
    IReadOnlyList<FrequentRoute> FrequentRoutes,
    IReadOnlyList<ZoneTiming> ZoneTimings);

/// <summary>Параметры анализа маршрутов.</summary>
/// <param name="MinDwellSeconds">Зоны, в которых игрок провёл меньше этого времени, считаются «проскоком» и выбрасываются.</param>
/// <param name="PrefixLength">Сколько первых зон сравнивать при поиске повторяющихся маршрутов.</param>
/// <param name="MinRoundsForFrequent">Минимум раундов для «частого» маршрута.</param>
public sealed record RouteOptions(float MinDwellSeconds = 1.0f, int PrefixLength = 3, int MinRoundsForFrequent = 2);

public interface IRouteAnalysisService
{
    RouteAnalysis Analyze(MatchData match, ulong steamId, RouteOptions? options = null);
}
