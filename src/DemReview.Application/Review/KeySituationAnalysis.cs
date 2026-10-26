using System.Numerics;
using DemReview.Application.Context;
using DemReview.Domain;

namespace DemReview.Application.Review;

/// <summary>Ключевая ситуация раунда.</summary>
public enum KeySituationKind
{
    /// <summary>Клатч: игрок остался последним живым в команде против одного или нескольких противников.</summary>
    Clutch,

    /// <summary>Пост-плант: T, бомба заложена, игрок жив.</summary>
    PostPlant,

    /// <summary>Ретейк: CT, бомба заложена, игрок жив.</summary>
    Retake,
}

/// <summary>Игрок в ключевой ситуации раунда.</summary>
/// <param name="Tick">Начало ситуации: гибель последнего союзника или закладка бомбы.</param>
/// <param name="Time">Секунды от конца фризтайма.</param>
/// <param name="Allies">Живые союзники в начале ситуации (без самого игрока).</param>
/// <param name="Enemies">Живые противники в начале ситуации.</param>
/// <param name="Survived">Игрок дожил до конца раунда.</param>
/// <param name="Kills">Убийства игрока с начала ситуации.</param>
/// <param name="Place">Где игрок провёл ситуацию: зона, в которой он был дольше всего.</param>
/// <param name="Position">Точка игрока в этой зоне.</param>
public sealed record KeySituation(
    KeySituationKind Kind,
    int Round,
    int Tick,
    float Time,
    Side Side,
    int Allies,
    int Enemies,
    bool Won,
    bool Survived,
    int Kills,
    string Place,
    Vector3 Position,
    BombSite Site);

/// <summary>Сколько раз игрок был в ситуации на этой позиции и сколько из этих раундов выиграла его команда.</summary>
public sealed record PlaceRecord(string Place, int Rounds, int Wins)
{
    public double WinRate => Rounds == 0 ? 0 : Wins / (double)Rounds;
}

/// <summary>
/// Клатчи, пост-планты и ретейки игрока: где он стоит в этих ситуациях и как часто его команда выигрывает раунд.
/// </summary>
public static class KeySituationAnalysis
{
    public static IReadOnlyList<KeySituation> Analyze(IMatchContext context, ulong steamId)
    {
        var match = context.Match;
        var result = new List<KeySituation>();
        foreach (var round in match.Rounds)
        {
            var side = match.FindPlayer(steamId)?.SideInRound(round.Number) ?? Side.None;
            if (side == Side.None || !context.IsAlive(steamId, round.Number, round.FreezeEndTick))
                continue;

            var kills = match.Kills.Where(k => k.Round == round.Number && k.Tick <= round.EndTick).OrderBy(k => k.Tick).ToList();
            if (Clutch(context, round, kills, steamId, side) is { } clutch)
                result.Add(clutch);

            var plant = match.Bombs.FirstOrDefault(b => b.Round == round.Number && b.Type == BombEventType.Planted && b.Tick <= round.EndTick);
            if (plant is not null && context.IsAlive(steamId, round.Number, plant.Tick))
            {
                var kind = side == Side.T ? KeySituationKind.PostPlant : KeySituationKind.Retake;
                result.Add(Create(context, kind, round, kills, steamId, side, plant.Tick, plant.Site));
            }
        }
        return result;
    }

    /// <summary>Позиции игрока в ситуации: зоны по числу раундов, сначала самые частые.</summary>
    public static IReadOnlyList<PlaceRecord> Places(IEnumerable<KeySituation> situations) =>
        situations
            .GroupBy(s => s.Place)
            .Select(g => new PlaceRecord(g.Key, g.Count(), g.Count(s => s.Won)))
            .OrderByDescending(p => p.Rounds)
            .ThenByDescending(p => p.Wins)
            .ThenBy(p => p.Place, StringComparer.Ordinal)
            .ToList();

    /// <summary>Клатч начинается, когда погиб последний союзник игрока, а противники ещё живы.</summary>
    private static KeySituation? Clutch(IMatchContext context, RoundInfo round, List<KillEvent> kills, ulong steamId, Side side)
    {
        var match = context.Match;
        foreach (var kill in kills)
        {
            if (kill.VictimSteamId == steamId)
                return null;
            if (match.FindPlayer(kill.VictimSteamId)?.SideInRound(round.Number) != side)
                continue;

            if (context.AliveCount(round.Number, side, kill.Tick) == 1 && context.AliveCount(round.Number, side.Opposite(), kill.Tick) > 0)
                return Create(context, KeySituationKind.Clutch, round, kills, steamId, side, kill.Tick, context.BombAt(round.Number, kill.Tick).Site);
        }
        return null;
    }

    private static KeySituation Create(IMatchContext context, KeySituationKind kind, RoundInfo round, List<KillEvent> kills, ulong steamId,
        Side side, int tick, BombSite site)
    {
        var match = context.Match;
        var death = kills.FirstOrDefault(k => k.VictimSteamId == steamId && k.Tick >= tick);
        var end = death?.Tick ?? round.EndTick;
        var (place, position) = MainPlace(match.TrackOf(steamId).InRound(round.Number).Where(s => s.Tick >= tick && s.Tick <= end).ToList(),
            match.TrackOf(steamId).InRound(round.Number).LastOrDefault(s => s.Tick <= tick));

        return new KeySituation(
            kind,
            round.Number,
            tick,
            (tick - round.FreezeEndTick) / (float)match.TickRate,
            side,
            context.AliveCount(round.Number, side, tick) - 1,
            context.AliveCount(round.Number, side.Opposite(), tick),
            round.Winner == side,
            death is null,
            kills.Count(k => k.AttackerSteamId == steamId && k.Tick >= tick &&
                             match.FindPlayer(k.VictimSteamId)?.SideInRound(round.Number) == side.Opposite()),
            place,
            position,
            site);
    }

    /// <summary>Зона, где игрок провёл больше всего сэмплов ситуации, и его средняя точка в ней.</summary>
    private static (string Place, Vector3 Position) MainPlace(List<PlayerSample> samples, PlayerSample fallback)
    {
        var inZone = samples.Where(s => s.Place.Length > 0).GroupBy(s => s.Place).MaxBy(g => g.Count());
        if (inZone is null)
            return (fallback.Place ?? "", fallback.Position);

        var points = inZone.ToList();
        var center = points.Aggregate(Vector3.Zero, (sum, s) => sum + s.Position) / points.Count;
        return (inZone.Key, center);
    }
}
