using DemReview.Application.Context;
using DemReview.Application.Routes;
using DemReview.Domain;

namespace DemReview.Application.Situations;

/// <summary>
/// «Условия → действия»: для каждого раунда собирает ситуацию (сторона, закупка, счёт) и решение игрока
/// (маршрут, итоговая позиция, гранаты, агрессия/пассив), затем строит частотные таблицы. Без ML.
/// </summary>
public sealed class SituationAnalysisService(IRoundContextService contexts, IRouteAnalysisService routes) : ISituationAnalysisService
{
    public SituationAnalysis Analyze(MatchData match, ulong steamId, SituationOptions? options = null)
    {
        options ??= new SituationOptions();
        var context = contexts.Create(match, options.Economy);
        var routeAnalysis = routes.Analyze(match, steamId, new RouteOptions(PrefixLength: options.RoutePrefixLength));
        var routesByRound = routeAnalysis.Rounds.ToDictionary(r => r.Round);
        var player = match.FindPlayer(steamId);

        var situations = new List<RoundSituation>();
        foreach (var round in match.Rounds)
        {
            var side = player?.SideInRound(round.Number) ?? Side.None;
            if (side == Side.None || !routesByRound.TryGetValue(round.Number, out var route))
                continue;

            situations.Add(Build(match, context, steamId, round, side, route, options));
        }

        return new SituationAnalysis(steamId, situations, Tables(situations));
    }

    private static RoundSituation Build(MatchData match, IMatchContext context, ulong steamId, RoundInfo round, Side side,
        RoundRoute route, SituationOptions options)
    {
        var economy = context.Economy(round.Number);
        var (scoreUs, scoreThem) = side == Side.CT ? (round.ScoreCTBefore, round.ScoreTBefore) : (round.ScoreTBefore, round.ScoreCTBefore);

        var contactTick = FirstContactTick(match, steamId, round.Number);
        float? contactTime = contactTick is { } t ? (t - round.FreezeEndTick) / (float)match.TickRate : null;
        var matchup = contactTick is { } ct ? context.At(steamId, round.Number, ct - 1).Matchup : "";

        var aggression = contactTime switch
        {
            null => Aggression.Passive,
            { } c when c <= options.AggressiveContactSeconds => Aggression.Aggressive,
            { } c when c >= options.PassiveContactSeconds => Aggression.Passive,
            _ => Aggression.Neutral,
        };

        var roundKills = match.Kills.Where(k => k.Round == round.Number).OrderBy(k => k.Tick).ToList();
        var firstKill = roundKills.FirstOrDefault(k => k.AttackerSteamId is not null && k.AttackerSteamId != k.VictimSteamId);
        var firstDuel = firstKill is null ? FirstDuel.NotInvolved
            : firstKill.AttackerSteamId == steamId ? FirstDuel.Won
            : firstKill.VictimSteamId == steamId ? FirstDuel.Lost
            : FirstDuel.NotInvolved;

        var grenades = match.GrenadesOf(steamId)
            .Where(g => g.Round == round.Number)
            .GroupBy(g => g.Type)
            .ToDictionary(g => g.Key, g => g.Count());

        return new RoundSituation(
            round.Number,
            side,
            economy?.PlayerBuy.GetValueOrDefault(steamId) ?? BuyType.Eco,
            economy?.Of(side).Buy ?? BuyType.Eco,
            economy?.Of(side.Opposite()).Buy ?? BuyType.Eco,
            economy?.PlayerEquipment.GetValueOrDefault(steamId) ?? 0,
            economy?.Of(side).AverageEquipment ?? 0,
            scoreUs,
            scoreThem,
            scoreUs == scoreThem ? ScoreState.Tied : scoreUs > scoreThem ? ScoreState.Leading : ScoreState.Trailing,
            round.IsOvertime,
            string.Join(" → ", route.ZoneNames.Take(options.RoutePrefixLength)),
            route.Zones.Count > 0 ? route.Zones[^1].Zone : "",
            grenades,
            contactTime,
            matchup,
            aggression,
            firstDuel,
            roundKills.Count(k => k.AttackerSteamId == steamId && k.VictimSteamId != steamId),
            route.Died,
            round.Winner == side);
    }

    /// <summary>Первый тик, когда игрок нанёс или получил урон (или участвовал в убийстве).</summary>
    private static int? FirstContactTick(MatchData match, ulong steamId, int round)
    {
        var damage = match.Damages
            .Where(d => d.Round == round && d.AttackerSteamId is not null && d.AttackerSteamId != d.VictimSteamId &&
                        (d.AttackerSteamId == steamId || d.VictimSteamId == steamId))
            .Select(d => (int?)d.Tick)
            .Min();
        var kill = match.Kills
            .Where(k => k.Round == round && (k.AttackerSteamId == steamId || k.VictimSteamId == steamId))
            .Select(k => (int?)k.Tick)
            .Min();
        return (damage, kill) switch
        {
            ({ } d, { } k) => Math.Min(d, k),
            ({ } d, null) => d,
            (null, { } k) => k,
            _ => null,
        };
    }

    // ---------- Частотные таблицы ----------

    public IReadOnlyList<FrequencyTable> BuildTables(IReadOnlyList<RoundSituation> rounds) => Tables(rounds);

    private static List<FrequencyTable> Tables(IReadOnlyList<RoundSituation> rounds)
    {
        static string SideBuy(RoundSituation s) => $"{Labels.Side(s.Side)} · {Labels.Buy(s.TeamBuy)}";

        return
        [
            Table("Закупка → маршрут", "Сторона и закупка команды", "Маршрут (первые зоны)", rounds, SideBuy,
                s => s.RouteKey.Length > 0 ? s.RouteKey : "нет данных"),
            Table("Закупка → агрессия", "Сторона и закупка команды", "Агрессия", rounds, SideBuy,
                s => Labels.Aggression(s.Aggression)),
            Table("Закупка → итоговая позиция", "Сторона и закупка команды", "Итоговая позиция", rounds, SideBuy,
                s => s.FinalZone.Length > 0 ? s.FinalZone : "нет данных"),
            Table("Закупка → гранаты", "Сторона и закупка команды", "Гранат за раунд", rounds, SideBuy,
                s => s.GrenadeCount switch { 0 => "0", <= 2 => "1–2", _ => "3+" }),
            Table("Счёт → агрессия", "Сторона и счёт", "Агрессия", rounds,
                s => $"{Labels.Side(s.Side)} · {Labels.Score(s.Score)}", s => Labels.Aggression(s.Aggression)),
            Table("Сторона → первая дуэль", "Сторона", "Первая дуэль раунда", rounds,
                s => Labels.Side(s.Side), s => Labels.FirstDuel(s.FirstDuel)),
        ];
    }

    internal static FrequencyTable Table(string name, string conditionTitle, string decisionTitle,
        IReadOnlyList<RoundSituation> rounds, Func<RoundSituation, string> condition, Func<RoundSituation, string> decision)
    {
        var rows = rounds
            .GroupBy(condition)
            .OrderBy(g => g.Key)
            .SelectMany(byCondition =>
            {
                var total = byCondition.Count();
                return byCondition
                    .GroupBy(decision)
                    .Select(g => new FrequencyRow(byCondition.Key, g.Key, g.Count(), total, g.Select(s => s.Round).ToList()))
                    .OrderByDescending(r => r.Count);
            })
            .ToList();
        return new FrequencyTable(name, conditionTitle, decisionTitle, rows);
    }
}

/// <summary>Русские подписи значений для таблиц и формулировок.</summary>
public static class Labels
{
    public static string Side(Side side) => side switch { Domain.Side.T => "T", Domain.Side.CT => "CT", _ => "—" };

    public static string Buy(BuyType buy) => buy switch
    {
        BuyType.Pistol => "пистолетка",
        BuyType.Eco => "эко",
        BuyType.Force => "форс",
        _ => "фулл-бай",
    };

    public static string Aggression(Aggression a) => a switch
    {
        Situations.Aggression.Aggressive => "агрессия",
        Situations.Aggression.Neutral => "нейтрально",
        _ => "пассив",
    };

    public static string Score(ScoreState s) => s switch
    {
        ScoreState.Leading => "ведём",
        ScoreState.Trailing => "отстаём",
        _ => "равный счёт",
    };

    public static string FirstDuel(FirstDuel d) => d switch
    {
        Situations.FirstDuel.Won => "выиграл",
        Situations.FirstDuel.Lost => "проиграл",
        _ => "не участвовал",
    };
}
