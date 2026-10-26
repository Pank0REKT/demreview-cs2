using System.Numerics;
using DemReview.Application.Context;
using DemReview.Domain;

namespace DemReview.Application.Review;

/// <summary>Спорный момент в смерти игрока.</summary>
public enum MistakeKind
{
    /// <summary>Смерть без размена: убийцу не убили в течение нескольких секунд, хотя союзники были живы.</summary>
    Untraded,

    /// <summary>Смерть в одиночку: ближайший живой союзник далеко.</summary>
    Isolated,

    /// <summary>Смерть с неброшенными гранатами.</summary>
    UnusedGrenades,

    /// <summary>Повторный пик: погиб вскоре после выигранной дуэли, не сменив позицию.</summary>
    RePeek,
}

/// <summary>Пороги поиска ошибок.</summary>
/// <param name="TradeSeconds">За сколько секунд после смерти союзник должен убить убийцу, чтобы смерть считалась разменянной.</param>
/// <param name="IsolationDistance">Ближайший живой союзник дальше — смерть в одиночку, units.</param>
/// <param name="RePeekSeconds">Смерть не позже этого после своего убийства — повторный пик, с.</param>
/// <param name="RePeekDistance">…если игрок погиб не дальше этого от места, где выиграл дуэль, units.</param>
public sealed record MistakeOptions(
    float TradeSeconds = 5,
    float IsolationDistance = 1500,
    float RePeekSeconds = 4,
    float RePeekDistance = 300);

/// <summary>Смерть игрока и что в ней спорного.</summary>
/// <param name="Time">Секунды от конца фризтайма.</param>
/// <param name="Place">Зона, где погиб игрок.</param>
/// <param name="AliveAllies">Сколько союзников было живо в момент смерти.</param>
/// <param name="TeammateDistance">Расстояние до ближайшего живого союзника; null — живых союзников не было.</param>
/// <param name="UnusedGrenades">Неброшенные гранаты; null — демка разобрана версией без этих данных.</param>
/// <param name="RePeekAfter">Через сколько секунд после своего убийства погиб (для повторного пика).</param>
/// <param name="RePeekVictim">Кого игрок убил перед повторным пиком.</param>
public sealed record DeathReview(
    KillEvent Death,
    Side Side,
    float Time,
    string Place,
    int AliveAllies,
    IReadOnlyList<MistakeKind> Mistakes,
    float? TeammateDistance,
    IReadOnlyList<GrenadeType>? UnusedGrenades,
    float? RePeekAfter,
    ulong? RePeekVictim)
{
    public int Round => Death.Round;

    public int Tick => Death.Tick;

    public ulong? KillerSteamId => Death.AttackerSteamId;

    /// <summary>Сколько стоили неброшенные гранаты, $.</summary>
    public int UnusedGrenadeValue => UnusedGrenades?.Sum(GrenadePrices.Of) ?? 0;

    public bool Has(MistakeKind kind) => Mistakes.Contains(kind);
}

/// <summary>Цены гранат в CS2, $.</summary>
public static class GrenadePrices
{
    public static int Of(GrenadeType type) => type switch
    {
        GrenadeType.HighExplosive => 300,
        GrenadeType.Flashbang => 200,
        GrenadeType.Smoke => 300,
        GrenadeType.Molotov => 400,
        GrenadeType.Incendiary => 500,
        GrenadeType.Decoy => 50,
        _ => 0,
    };
}

/// <summary>
/// Ошибки игрока по его смертям: без размена, в одиночку, с неброшенными гранатами, повторный пик после
/// выигранной дуэли. Одна смерть может попасть в несколько категорий.
/// </summary>
public static class MistakeAnalysis
{
    public static IReadOnlyList<DeathReview> Analyze(IMatchContext context, ulong steamId, MistakeOptions? options = null)
    {
        options ??= new MistakeOptions();
        var match = context.Match;
        var result = new List<DeathReview>();
        var killsByRound = match.Kills.GroupBy(k => k.Round).ToDictionary(g => g.Key, g => g.OrderBy(k => k.Tick).ToList());

        foreach (var death in match.Kills.Where(k => k.VictimSteamId == steamId).OrderBy(k => k.Tick))
        {
            if (match.FindRound(death.Round) is not { } round || death.Tick > round.EndTick)
                continue;

            var side = match.FindPlayer(steamId)?.SideInRound(death.Round) ?? Side.None;
            if (side == Side.None)
                continue;

            var kills = killsByRound.GetValueOrDefault(death.Round, []);
            var position = DeathPosition(match, death);
            var mistakes = new List<MistakeKind>();

            var aliveAllies = context.AliveCount(death.Round, side, death.Tick);
            var killer = EnemyKiller(match, death, side);
            if (killer is { } k && aliveAllies > 0 && !IsTraded(match, kills, death, k, side, options))
                mistakes.Add(MistakeKind.Untraded);

            var teammateDistance = NearestTeammate(context, death, side, position);
            if (teammateDistance > options.IsolationDistance)
                mistakes.Add(MistakeKind.Isolated);

            if (death.VictimGrenades is { Count: > 0 })
                mistakes.Add(MistakeKind.UnusedGrenades);

            var rePeek = killer is null ? null : RePeek(match, kills, death, killer.Value, side, position, options);
            if (rePeek is not null)
                mistakes.Add(MistakeKind.RePeek);

            if (mistakes.Count == 0)
                continue;

            var sample = match.TrackOf(steamId).InRound(death.Round).LastOrDefault(s => s.Tick <= death.Tick);
            result.Add(new DeathReview(
                death,
                side,
                (death.Tick - round.FreezeEndTick) / (float)match.TickRate,
                sample.Place ?? "",
                aliveAllies,
                mistakes,
                teammateDistance,
                death.VictimGrenades,
                rePeek?.Seconds,
                rePeek?.Victim));
        }

        return result;
    }

    /// <summary>Убийца-противник; самоубийство, смерть от мира и от союзника — null.</summary>
    private static ulong? EnemyKiller(MatchData match, KillEvent death, Side side) =>
        death.AttackerSteamId is { } attacker && attacker != death.VictimSteamId &&
        match.FindPlayer(attacker)?.SideInRound(death.Round) == side.Opposite()
            ? attacker
            : null;

    /// <summary>Союзник убил убийцу в течение нескольких секунд после смерти.</summary>
    private static bool IsTraded(MatchData match, List<KillEvent> kills, KillEvent death, ulong killer, Side side, MistakeOptions options)
    {
        var until = death.Tick + options.TradeSeconds * match.TickRate;
        return kills.Any(k => k.VictimSteamId == killer && k.Tick >= death.Tick && k.Tick <= until &&
                              k.AttackerSteamId is { } a && a != death.VictimSteamId &&
                              match.FindPlayer(a)?.SideInRound(death.Round) == side);
    }

    /// <summary>Расстояние до ближайшего живого союзника в момент смерти; null — живых союзников нет.</summary>
    private static float? NearestTeammate(IMatchContext context, KillEvent death, Side side, Vector3 position)
    {
        var match = context.Match;
        float? nearest = null;
        foreach (var mate in match.Players)
        {
            if (mate.SteamId == death.VictimSteamId || mate.SideInRound(death.Round) != side ||
                !context.IsAlive(mate.SteamId, death.Round, death.Tick))
                continue;

            var samples = match.TrackOf(mate.SteamId).InRound(death.Round).TakeWhile(s => s.Tick <= death.Tick).ToList();
            if (samples.Count == 0)
                continue;
            var distance = Vector3.Distance(samples[^1].Position, position);
            if (nearest is null || distance < nearest)
                nearest = distance;
        }
        return nearest;
    }

    /// <summary>
    /// Повторный пик: незадолго до смерти игрок убил противника и погиб почти там же от другого противника —
    /// после выигранной дуэли не сменил позицию.
    /// </summary>
    private static (float Seconds, ulong Victim)? RePeek(MatchData match, List<KillEvent> kills, KillEvent death, ulong killer, Side side,
        Vector3 position, MistakeOptions options)
    {
        var from = death.Tick - options.RePeekSeconds * match.TickRate;
        var won = kills.LastOrDefault(k => k.AttackerSteamId == death.VictimSteamId && k.Tick < death.Tick && k.Tick >= from &&
                                           k.VictimSteamId != killer && match.FindPlayer(k.VictimSteamId)?.SideInRound(death.Round) == side.Opposite());
        if (won is null)
            return null;

        var duelPosition = won.AttackerPosition ?? match.TrackOf(death.VictimSteamId).InRound(death.Round)
            .LastOrDefault(s => s.Tick <= won.Tick).Position;
        if (Vector3.Distance(duelPosition, position) > options.RePeekDistance)
            return null;

        return ((death.Tick - won.Tick) / (float)match.TickRate, won.VictimSteamId);
    }

    /// <summary>Место смерти; если позиции в событии нет — последний сэмпл игрока.</summary>
    internal static Vector3 DeathPosition(MatchData match, KillEvent death) =>
        death.VictimPosition != Vector3.Zero
            ? death.VictimPosition
            : match.TrackOf(death.VictimSteamId).InRound(death.Round).LastOrDefault(s => s.Tick <= death.Tick).Position;
}
