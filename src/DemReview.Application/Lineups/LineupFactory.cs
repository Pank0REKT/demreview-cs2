using DemReview.Domain;

namespace DemReview.Application.Lineups;

public static class LineupFactory
{
    /// <summary>
    /// Раскидка из броска в демке. В сводном матче из нескольких демок указываются исходная демка
    /// и номер раунда в ней, а не общий номер.
    /// </summary>
    /// <param name="landZone">Зона, куда прилетела граната (пусто — неизвестно).</param>
    public static Lineup FromThrow(MatchData match, GrenadeThrow g, string landZone, DateTime savedUtc)
    {
        var part = match.PartOf(g.Round);
        var source = part?.FileName ?? Path.GetFileName(match.SourcePath);
        var round = part is null ? g.Round : g.Round - part.RoundOffset;
        var from = g.ThrowerPlace;
        var title = from.Length > 0 || landZone.Length > 0
            ? $"{(from.Length > 0 ? from : "?")} → {(landZone.Length > 0 ? landZone : "?")}"
            : match.MapName;

        return new Lineup(0, match.MapName, g.Type, g.ThrowerSide, title, g.ThrowerPosition, g.ThrowerPitch, g.ThrowerYaw,
            g.ThrowerMovement, g.ThrowerSpeed, g.DetonatePosition, from, landZone, match.FindPlayer(g.ThrowerSteamId)?.Name ?? "",
            g.ThrowerSteamId, source, round, g.ThrowTick, savedUtc);
    }
}
