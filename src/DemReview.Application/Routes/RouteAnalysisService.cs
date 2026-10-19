using DemReview.Domain;

namespace DemReview.Application.Routes;

public sealed class RouteAnalysisService : IRouteAnalysisService
{
    public RouteAnalysis Analyze(MatchData match, ulong steamId, RouteOptions? options = null)
    {
        options ??= new RouteOptions();
        var player = match.FindPlayer(steamId);
        var track = match.TrackOf(steamId);
        var deathsByRound = match.Kills.Where(k => k.VictimSteamId == steamId).Select(k => k.Round).ToHashSet();

        var rounds = track.Samples
            .GroupBy(s => s.Round)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var path = g.OrderBy(s => s.Tick).ToList();
                var side = player?.SideInRound(g.Key) ?? Side.None;
                return new RoundRoute(g.Key, side, BuildZones(path, options.MinDwellSeconds), path, deathsByRound.Contains(g.Key));
            })
            .ToList();

        return new RouteAnalysis(steamId, rounds, FindFrequent(rounds, options), ZoneTimings(rounds));
    }

    /// <summary>
    /// Последовательность зон: подряд идущие сэмплы одной зоны схлопываются, короткие посещения
    /// (меньше <paramref name="minDwell"/>) выбрасываются, кроме первой и последней зоны.
    /// </summary>
    internal static IReadOnlyList<ZoneVisit> BuildZones(IReadOnlyList<PlayerSample> path, float minDwell)
    {
        var raw = new List<ZoneVisit>();
        foreach (var sample in path)
        {
            if (string.IsNullOrEmpty(sample.Place))
                continue;

            if (raw.Count > 0 && raw[^1].Zone == sample.Place)
                raw[^1] = raw[^1] with { Leave = sample.TimeInRound };
            else
                raw.Add(new ZoneVisit(sample.Place, sample.TimeInRound, sample.TimeInRound));
        }

        // Длительность посещения считаем до прихода в следующую зону, а не до последнего сэмпла в ней.
        for (var i = 0; i < raw.Count - 1; i++)
            raw[i] = raw[i] with { Leave = raw[i + 1].Arrival };

        var result = new List<ZoneVisit>();
        for (var i = 0; i < raw.Count; i++)
        {
            var visit = raw[i];
            var isEdge = i == 0 || i == raw.Count - 1;
            if (!isEdge && visit.Duration < minDwell)
                continue;

            // После выброса проскока соседние посещения одной зоны объединяются.
            if (result.Count > 0 && result[^1].Zone == visit.Zone)
                result[^1] = result[^1] with { Leave = visit.Leave };
            else
                result.Add(visit);
        }

        return result;
    }

    private static List<FrequentRoute> FindFrequent(IReadOnlyList<RoundRoute> rounds, RouteOptions options)
    {
        var result = new List<FrequentRoute>();
        foreach (var bySide in rounds.Where(r => r.Side != Side.None).GroupBy(r => r.Side))
        {
            var total = bySide.Count();
            var groups = bySide
                .Where(r => r.Zones.Count > 0)
                .GroupBy(r => string.Join(" → ", r.ZoneNames.Take(options.PrefixLength)));

            foreach (var group in groups)
            {
                var roundNumbers = group.Select(r => r.Round).ToList();
                if (roundNumbers.Count < options.MinRoundsForFrequent)
                    continue;

                var zones = group.First().ZoneNames.Take(options.PrefixLength).ToList();
                result.Add(new FrequentRoute(bySide.Key, zones, roundNumbers, (double)roundNumbers.Count / total));
            }
        }

        return result.OrderByDescending(r => r.Share).ThenByDescending(r => r.Rounds.Count).ToList();
    }

    private static List<ZoneTiming> ZoneTimings(IReadOnlyList<RoundRoute> rounds) =>
        rounds
            .Where(r => r.Side != Side.None)
            .SelectMany(r => r.Zones
                .GroupBy(z => z.Zone)
                .Select(g => (r.Side, Zone: g.Key, Arrival: g.Min(z => z.Arrival))))
            .GroupBy(x => (x.Side, x.Zone))
            .Select(g =>
            {
                var times = g.Select(x => x.Arrival).Order().ToList();
                return new ZoneTiming(g.Key.Side, g.Key.Zone, times.Count, Median(times), times[0], times[^1]);
            })
            .OrderBy(t => t.Side)
            .ThenBy(t => t.MedianArrival)
            .ToList();

    private static float Median(IReadOnlyList<float> sorted) =>
        sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
}
