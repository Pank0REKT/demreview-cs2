using DemReview.Application.Context;
using DemReview.Domain;

namespace DemReview.Application.Review;

/// <summary>Разбор игры одного игрока: ошибки, ключевые ситуации и дуэли.</summary>
/// <param name="Deaths">Все смерти игрока за матч (в том числе без ошибок).</param>
/// <param name="HasGrenadeData">Демка разобрана версией, которая записывает неброшенные гранаты.</param>
/// <param name="HasDemoTicks">Для раундов известен тик демки — работает demo_gototick.</param>
public sealed record SelfReview(
    ulong SteamId,
    int Deaths,
    IReadOnlyList<DeathReview> Mistakes,
    IReadOnlyList<KeySituation> Situations,
    IReadOnlyList<Duel> Duels,
    bool HasGrenadeData,
    bool HasDemoTicks)
{
    public int Count(MistakeKind kind) => Mistakes.Count(m => m.Has(kind));

    public IEnumerable<KeySituation> Of(KeySituationKind kind) => Situations.Where(s => s.Kind == kind);

    public static SelfReview Analyze(IMatchContext context, ulong steamId, MistakeOptions? options = null)
    {
        var match = context.Match;
        var deaths = match.Kills.Where(k => k.VictimSteamId == steamId).ToList();
        return new SelfReview(
            steamId,
            deaths.Count,
            MistakeAnalysis.Analyze(context, steamId, options),
            KeySituationAnalysis.Analyze(context, steamId),
            DuelAnalysis.Analyze(match, steamId),
            deaths.Count == 0 || deaths.All(d => d.VictimGrenades is not null),
            match.Rounds.All(r => r.DemoTickOffset is not null));
    }
}

/// <summary>Команды консоли CS2 для перехода к моменту демки.</summary>
public static class DemoCommands
{
    /// <summary>
    /// «demo_gototick N» за <paramref name="leadSeconds"/> до момента (но не раньше начала раунда);
    /// null — для раунда нет тика демки (демка разобрана старой версией).
    /// </summary>
    public static string? GoToTick(MatchData match, int round, int tick, float leadSeconds)
    {
        if (match.FindRound(round) is not { } info)
            return null;
        var target = Math.Max(info.StartTick, tick - (int)(leadSeconds * match.TickRate));
        return info.DemoTickOf(target) is { } demoTick ? $"demo_gototick {demoTick}" : null;
    }
}
