using DemReview.Application.Context;
using DemReview.Application.Damage;
using DemReview.Application.Localization;
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

        var damage = DamageAnalysis.DealtByRound(DamageAnalysis.EffectiveHits(match), steamId);
        var situations = new List<RoundSituation>();
        foreach (var round in match.Rounds)
        {
            var side = player?.SideInRound(round.Number) ?? Side.None;
            if (side == Side.None || !routesByRound.TryGetValue(round.Number, out var route))
                continue;

            situations.Add(Build(match, context, steamId, round, side, route, options, damage.GetValueOrDefault(round.Number)));
        }

        return new SituationAnalysis(steamId, situations, Tables(situations));
    }

    private static RoundSituation Build(MatchData match, IMatchContext context, ulong steamId, RoundInfo round, Side side,
        RoundRoute route, SituationOptions options, int damage)
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
            damage,
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
        var sideBuy = Loc.T("Сторона и закупка команды", "Side and team buy");
        var aggression = Loc.T("Агрессия", "Aggression");
        var noData = Loc.T("нет данных", "no data");

        return
        [
            Table(Loc.T("Закупка → маршрут", "Buy → route"), sideBuy, Loc.T("Маршрут (первые зоны)", "Route (first zones)"), rounds, SideBuy,
                s => s.RouteKey.Length > 0 ? s.RouteKey : noData),
            Table(Loc.T("Закупка → агрессия", "Buy → aggression"), sideBuy, aggression, rounds, SideBuy,
                s => Labels.Aggression(s.Aggression)),
            Table(Loc.T("Закупка → итоговая позиция", "Buy → final position"), sideBuy, Loc.T("Итоговая позиция", "Final position"), rounds, SideBuy,
                s => s.FinalZone.Length > 0 ? s.FinalZone : noData),
            Table(Loc.T("Закупка → гранаты", "Buy → grenades"), sideBuy, Loc.T("Гранат за раунд", "Grenades per round"), rounds, SideBuy,
                s => s.GrenadeCount switch { 0 => "0", <= 2 => "1–2", _ => "3+" }),
            Table(Loc.T("Счёт → агрессия", "Score → aggression"), Loc.T("Сторона и счёт", "Side and score"), aggression, rounds,
                s => $"{Labels.Side(s.Side)} · {Labels.Score(s.Score)}", s => Labels.Aggression(s.Aggression)),
            Table(Loc.T("Сторона → первая дуэль", "Side → opening duel"), Loc.T("Сторона", "Side"), Loc.T("Первая дуэль раунда", "Opening duel of the round"), rounds,
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

/// <summary>Подписи значений для таблиц и формулировок на текущем языке.</summary>
public static class Labels
{
    public static string Side(Side side) => side switch { Domain.Side.T => "T", Domain.Side.CT => "CT", _ => "—" };

    /// <summary>Время от конца фризтайма в формате м:сс.</summary>
    public static string Time(float seconds)
    {
        var total = (int)Math.Round(Math.Max(0, seconds));
        return $"{total / 60}:{total % 60:00}";
    }

    public static string Buy(BuyType buy) => buy switch
    {
        BuyType.Pistol => Loc.T("пистолетка", "pistol"),
        BuyType.Eco => Loc.T("эко", "eco"),
        BuyType.Force => Loc.T("форс", "force buy"),
        _ => Loc.T("фулл-бай", "full buy"),
    };

    public static string Grenade(GrenadeType type) => type switch
    {
        GrenadeType.HighExplosive => "HE",
        GrenadeType.Flashbang => Loc.T("Флешка", "Flashbang"),
        GrenadeType.Smoke => Loc.T("Дым", "Smoke"),
        GrenadeType.Molotov => Loc.T("Молотов", "Molotov"),
        GrenadeType.Incendiary => Loc.T("Зажигательная", "Incendiary"),
        _ => Loc.T("Ложная", "Decoy"),
    };

    public static string Aggression(Aggression a) => a switch
    {
        Situations.Aggression.Aggressive => Loc.T("агрессия", "aggressive"),
        Situations.Aggression.Neutral => Loc.T("нейтрально", "neutral"),
        _ => Loc.T("пассив", "passive"),
    };

    public static string Score(ScoreState s) => s switch
    {
        ScoreState.Leading => Loc.T("ведём", "leading"),
        ScoreState.Trailing => Loc.T("отстаём", "trailing"),
        _ => Loc.T("равный счёт", "tied"),
    };

    public static string FirstDuel(FirstDuel d) => d switch
    {
        Situations.FirstDuel.Won => Loc.T("выиграл", "won"),
        Situations.FirstDuel.Lost => Loc.T("проиграл", "lost"),
        _ => Loc.T("не участвовал", "not involved"),
    };
}
