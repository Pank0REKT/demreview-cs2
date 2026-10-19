using System.Numerics;
using DemReview.Application.Maps;
using DemReview.Domain;

namespace DemReview.Application.Grenades;

public sealed class GrenadeAnalysisService : IGrenadeAnalysisService
{
    public GrenadeAnalysis Analyze(MatchData match, ulong steamId, GrenadeClusterOptions? options = null)
    {
        options ??= new GrenadeClusterOptions();
        var throws = match.GrenadesOf(steamId).OrderBy(g => g.ThrowTick).ToList();
        var zones = new ZoneLocator(match);
        var landZones = throws
            .Where(t => t.DetonatePosition.HasValue)
            .ToDictionary(t => t.Id, t => zones.Locate(t.DetonatePosition!.Value));

        var clusters = Cluster(throws, options)
            .Select((group, index) => ToCluster(index + 1, group, landZones, zones))
            .OrderByDescending(c => c.Throws.Count)
            .ThenBy(c => c.MedianThrowTime)
            .ToList();

        var summary = throws
            .GroupBy(t => (t.Type, t.ThrowerSide))
            .Select(g => new GrenadeTypeSummary(g.Key.Type, g.Key.ThrowerSide, g.Count(),
                g.Select(t => t.Round).Distinct().Count(), Median(g.Select(t => t.ThrowTime))))
            .OrderBy(s => s.Side)
            .ThenByDescending(s => s.Count)
            .ToList();

        return new GrenadeAnalysis(steamId, throws, clusters, summary, Flashes(throws), landZones);
    }

    /// <summary>
    /// Жадная кластеризация: бросок попадает в кластер того же типа и стороны, если его точка броска
    /// и точка детонации ближе порогов к средним точкам кластера. Гранаты без детонации не кластеризуются.
    /// </summary>
    internal static List<List<GrenadeThrow>> Cluster(IEnumerable<GrenadeThrow> throws, GrenadeClusterOptions options)
    {
        var clusters = new List<(GrenadeType Type, Side Side, Vector3 From, Vector3 To, List<GrenadeThrow> Items)>();

        foreach (var t in throws.Where(t => t.DetonatePosition.HasValue))
        {
            var land = t.DetonatePosition!.Value;
            var index = clusters.FindIndex(c =>
                c.Type == t.Type && c.Side == t.ThrowerSide &&
                Vector3.Distance(c.From, t.ThrowerPosition) <= options.ThrowDistance &&
                Vector3.Distance(c.To, land) <= options.LandDistance);

            if (index < 0)
            {
                clusters.Add((t.Type, t.ThrowerSide, t.ThrowerPosition, land, [t]));
                continue;
            }

            var c = clusters[index];
            c.Items.Add(t);
            var n = c.Items.Count;
            clusters[index] = c with
            {
                From = c.From + (t.ThrowerPosition - c.From) / n,
                To = c.To + (land - c.To) / n,
            };
        }

        return clusters.Where(c => c.Items.Count >= 2).Select(c => c.Items).ToList();
    }

    private static GrenadeCluster ToCluster(int id, List<GrenadeThrow> items, IReadOnlyDictionary<int, string> landZones,
        ZoneLocator zones)
    {
        var from = Average(items.Select(t => t.ThrowerPosition));
        var to = Average(items.Select(t => t.DetonatePosition!.Value));
        var fromZone = MostCommon(items.Select(t => t.ThrowerPlace)) is { Length: > 0 } place ? place : zones.Locate(from);
        var toZone = MostCommon(items.Select(t => landZones.GetValueOrDefault(t.Id, "")));
        return new GrenadeCluster(id, items[0].Type, items[0].ThrowerSide, from, to, fromZone, toZone, items,
            Median(items.Select(t => t.ThrowTime)));
    }

    private static FlashSummary Flashes(IReadOnlyList<GrenadeThrow> throws)
    {
        var flashes = throws.Where(t => t.Type == GrenadeType.Flashbang).ToList();
        var victims = flashes.SelectMany(f => f.FlashVictims).ToList();
        var enemies = victims.Where(v => !v.IsTeammate).ToList();
        var mates = victims.Where(v => v.IsTeammate && !v.IsSelf).ToList();
        return new FlashSummary(
            flashes.Count,
            enemies.Count,
            enemies.Sum(v => v.Duration),
            mates.Count,
            mates.Sum(v => v.Duration),
            victims.Count(v => v.IsSelf),
            flashes.Count(f => f.FlashVictims.All(v => v.IsTeammate)));
    }

    private static Vector3 Average(IEnumerable<Vector3> points)
    {
        var list = points.ToList();
        return list.Aggregate(Vector3.Zero, (a, b) => a + b) / list.Count;
    }

    private static string MostCommon(IEnumerable<string> values) =>
        values.Where(v => v.Length > 0).GroupBy(v => v).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault() ?? "";

    private static float Median(IEnumerable<float> values)
    {
        var sorted = values.Order().ToList();
        if (sorted.Count == 0)
            return 0;
        return sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
    }
}
