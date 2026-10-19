using System.Numerics;
using DemReview.Application.Context;
using DemReview.Application.Grenades;
using DemReview.Application.Localization;
using DemReview.Application.Maps;
using DemReview.Application.Routes;
using DemReview.Application.Situations;
using DemReview.Domain;

namespace DemReview.Application.Patterns;

/// <summary>
/// Выявляет устойчивые повторяющиеся поведения игрока по результатам анализа маршрутов, гранат, контекста и
/// «условий → действий». Паттерн показывается, только если частота не ниже порога и примеров не меньше минимума;
/// уверенность учитывает малую выборку (интервал Уилсона).
/// </summary>
public sealed class PlayerPatternService(
    ISituationAnalysisService situations,
    IRouteAnalysisService routes,
    IGrenadeAnalysisService grenades,
    IRoundContextService contexts) : IPlayerPatternService
{
    /// <summary>Окна времени для реакций, секунды.</summary>
    private const float TradeWindow = 8;
    private const float RotationSignalWindow = 8;
    private const float FlashContactWindow = 3;

    public PlayerProfile BuildProfile(MatchData match, ulong steamId, PatternOptions? options = null)
    {
        options ??= new PatternOptions();
        var data = new ProfileData(match, steamId, options, situations, routes, grenades, contacts: contexts);
        var candidates = new List<Pattern>();

        foreach (var side in new[] { Side.T, Side.CT })
        {
            if (data.SideRounds(side).Count == 0)
                continue;

            RoutePatterns(data, side, candidates);
            PositionPatterns(data, side, candidates);
            TimingPatterns(data, side, candidates);
            AggressionPatterns(data, side, candidates);
            if (side == Side.CT)
                RotationPatterns(data, candidates);
            ReactionPatterns(data, side, candidates);
            GrenadePatterns(data, side, candidates);
            DeathPatterns(data, side, candidates);
        }

        var patterns = candidates
            .Where(p => p.Share >= options.MinShare && p.Count >= options.MinExamples)
            .GroupBy(p => p.Text)
            .Select(g => g.First())
            .OrderByDescending(p => p.Share)
            .ThenByDescending(p => p.ConfidenceScore)
            .ToList();

        var metrics = new[] { Side.T, Side.CT }
            .Where(s => data.SideRounds(s).Count > 0)
            .Select(s => data.Metrics(steamId, s))
            .ToList();

        return new PlayerProfile(
            steamId,
            match.FindPlayer(steamId)?.Name ?? "",
            match.MapName,
            metrics.Select(m => ClassifyRole(data, m)).ToList(),
            metrics,
            patterns,
            Predictability(data.Situations),
            data.Situations.Count,
            options);
    }

    // ---------- Маршруты и позиции ----------

    /// <summary>Начало формулировки: «На T при закупке «эко»» / «On T (eco)».</summary>
    private static string On(Side side, string? qualifierRu = null, string? qualifierEn = null) =>
        Loc.T($"На {Labels.Side(side)}{(qualifierRu is null ? "" : $" {qualifierRu}")}",
            $"On {Labels.Side(side)}{(qualifierEn is null ? "" : $" ({qualifierEn})")}");

    private static string OnBuy(Side side, BuyType buy) => On(side, $"при закупке «{Labels.Buy(buy)}»", Labels.Buy(buy));

    private static void RoutePatterns(ProfileData d, Side side, List<Pattern> output)
    {
        var rounds = d.SideRounds(side).Where(r => r.RouteKey.Length > 0).ToList();
        var on = On(side);
        foreach (var group in rounds.GroupBy(r => r.RouteKey))
        {
            output.Add(d.Make(PatternCategory.Route, side, $"{Labels.Side(side)}", Loc.T($"маршрут {group.Key}", $"route {group.Key}"),
                Loc.T($"{on} в {{0}} из {{1}} раундов идёт {group.Key}", $"{on}: in {{0}} of {{1}} rounds takes {group.Key}"),
                group.Select(r => r.Round), rounds.Count));
        }

        // По типу закупки — только если условие даёт заметно более устойчивое поведение.
        foreach (var byBuy in rounds.GroupBy(r => r.TeamBuy))
        {
            var buyRounds = byBuy.ToList();
            var onBuy = OnBuy(side, byBuy.Key);
            foreach (var group in buyRounds.GroupBy(r => r.RouteKey))
            {
                var overall = (double)rounds.Count(r => r.RouteKey == group.Key) / rounds.Count;
                if ((double)group.Count() / buyRounds.Count < overall + 0.1)
                    continue;
                output.Add(d.Make(PatternCategory.Route, side, $"{Labels.Side(side)} · {Labels.Buy(byBuy.Key)}",
                    Loc.T($"маршрут {group.Key}", $"route {group.Key}"),
                    Loc.T($"{onBuy} в {{0}} из {{1}} раундов идёт {group.Key}", $"{onBuy}: in {{0}} of {{1}} rounds takes {group.Key}"),
                    group.Select(r => r.Round), buyRounds.Count));
            }
        }
    }

    private static void PositionPatterns(ProfileData d, Side side, List<Pattern> output)
    {
        var rounds = d.SideRounds(side);
        var on = On(side);
        var anchors = rounds.Select(r => (r.Round, Zone: d.AnchorZone(r.Round))).Where(x => x.Zone is not null).ToList();
        foreach (var group in anchors.GroupBy(x => x.Zone!))
        {
            output.Add(d.Make(PatternCategory.Position, side, Labels.Side(side), Loc.T($"держит {group.Key}", $"holds {group.Key}"),
                Loc.T($"{on} в {{0}} из {{1}} раундов в начале раунда занимает {group.Key}",
                    $"{on}: in {{0}} of {{1}} rounds sets up at {group.Key} early in the round"),
                group.Select(x => x.Round), rounds.Count));
        }

        var finals = rounds.Where(r => r.FinalZone.Length > 0).ToList();
        foreach (var group in finals.GroupBy(r => r.FinalZone))
        {
            output.Add(d.Make(PatternCategory.Position, side, Labels.Side(side),
                Loc.T($"итоговая позиция {group.Key}", $"final position {group.Key}"),
                Loc.T($"{on} в {{0}} из {{1}} раундов заканчивает раунд в {group.Key}", $"{on}: in {{0}} of {{1}} rounds ends the round in {group.Key}"),
                group.Select(r => r.Round), finals.Count));
        }
    }

    private static void TimingPatterns(ProfileData d, Side side, List<Pattern> output)
    {
        var rounds = d.SideRounds(side);
        var on = On(side);
        var visits = d.Routes.Rounds
            .Where(r => r.Side == side)
            .SelectMany(r => r.Zones
                .Where(z => !IsSpawn(z.Zone))
                .GroupBy(z => z.Zone)
                .Select(g => (r.Round, Zone: g.Key, Arrival: g.Min(z => z.Arrival))))
            .GroupBy(v => v.Zone);

        foreach (var zone in visits)
        {
            var times = zone.Select(v => v.Arrival).Order().ToList();
            // Зоны у самого спавна (приход раньше 3 с) ничего не говорят о решениях игрока.
            if (times.Count < 2 || times[^1] - times[0] > 15 || PatternStats.Median(times) < 3)
                continue;

            var (median, from, to) = (Labels.Time(PatternStats.Median(times)), Labels.Time(times[0]), Labels.Time(times[^1]));
            output.Add(d.Make(PatternCategory.Timing, side, Labels.Side(side), Loc.T($"выход в {zone.Key}", $"reaches {zone.Key}"),
                Loc.T($"{on} в {{0}} из {{1}} раундов выходит в {zone.Key} на ~{median} (разброс {from}–{to})",
                    $"{on}: in {{0}} of {{1}} rounds reaches {zone.Key} at ~{median} (range {from}–{to})"),
                zone.Select(v => v.Round), rounds.Count));
        }

        var contacts = rounds.Where(r => r.FirstContactTime is not null).ToList();
        if (contacts.Count >= 2)
        {
            var times = contacts.Select(r => r.FirstContactTime!.Value).Order().ToList();
            var median = PatternStats.Median(times);
            var near = contacts.Where(r => Math.Abs(r.FirstContactTime!.Value - median) <= 10).ToList();
            output.Add(d.Make(PatternCategory.Timing, side, Labels.Side(side), Loc.T("время первого боя", "first fight time"),
                Loc.T($"{on} в {{0}} из {{1}} раундов вступает в первый бой на ~{Labels.Time(median)} (±10 с)",
                    $"{on}: in {{0}} of {{1}} rounds takes the first fight at ~{Labels.Time(median)} (±10 s)"),
                near.Select(r => r.Round), rounds.Count));
        }
    }

    private static void AggressionPatterns(ProfileData d, Side side, List<Pattern> output)
    {
        var rounds = d.SideRounds(side);
        void Add(string condition, string on, IReadOnlyList<RoundSituation> subset)
        {
            foreach (var group in subset.GroupBy(r => r.Aggression))
            {
                output.Add(d.Make(PatternCategory.Aggression, side, condition, Labels.Aggression(group.Key),
                    Loc.T($"{on} в {{0}} из {{1}} раундов играет {AggressionText(group.Key)}",
                        $"{on}: in {{0}} of {{1}} rounds plays {AggressionText(group.Key)}"),
                    group.Select(r => r.Round), subset.Count));
            }
        }

        Add(Labels.Side(side), On(side), rounds);
        foreach (var byBuy in rounds.GroupBy(r => r.TeamBuy))
            Add($"{Labels.Side(side)} · {Labels.Buy(byBuy.Key)}", OnBuy(side, byBuy.Key), byBuy.ToList());
        foreach (var byScore in rounds.GroupBy(r => r.Score))
            Add($"{Labels.Side(side)} · {Labels.Score(byScore.Key)}", OnScore(side, byScore.Key), byScore.ToList());

        var on = On(side);
        var duels = rounds.Where(r => r.FirstDuel != FirstDuel.NotInvolved).ToList();
        output.Add(d.Make(PatternCategory.Aggression, side, Labels.Side(side), Loc.T("участвует в первой дуэли", "takes the opening duel"),
            Loc.T($"{on} в {{0}} из {{1}} раундов участвует в первой дуэли раунда", $"{on}: takes the opening duel in {{0}} of {{1}} rounds"),
            duels.Select(r => r.Round), rounds.Count));
        if (duels.Count > 0)
        {
            var won = duels.Where(r => r.FirstDuel == FirstDuel.Won);
            var lost = duels.Where(r => r.FirstDuel == FirstDuel.Lost);
            var condition = Loc.T($"{Labels.Side(side)} · первая дуэль", $"{Labels.Side(side)} · opening duel");
            output.Add(d.Make(PatternCategory.Aggression, side, condition, Loc.T("выигрывает", "wins"),
                Loc.T($"{on} выигрывает первую дуэль раунда в {{0}} из {{1}} случаев", $"{on}: wins the opening duel in {{0}} of {{1}} cases"),
                won.Select(r => r.Round), duels.Count));
            output.Add(d.Make(PatternCategory.Aggression, side, condition, Loc.T("проигрывает", "loses"),
                Loc.T($"{on} проигрывает первую дуэль раунда в {{0}} из {{1}} случаев", $"{on}: loses the opening duel in {{0}} of {{1}} cases"),
                lost.Select(r => r.Round), duels.Count));
        }
    }

    // ---------- Ротации на CT ----------

    /// <summary>
    /// Ротации: игрок держал зону в начале раунда и покинул её. Сигнал — событие за последние 8 с до ухода:
    /// закладка бомбы, смерть тиммейта, контакт команды. Паттерн: при сигнале X уходит в Y% случаев.
    /// </summary>
    private static void RotationPatterns(ProfileData d, List<Pattern> output)
    {
        var bySignal = new Dictionary<string, (int Occurred, List<(int Round, string From, string Via, float Time)> Left)>();

        foreach (var round in d.SideRounds(Side.CT))
        {
            if (d.AnchorZone(round.Round) is not { } anchor)
                continue;

            var leave = d.LeaveTime(round.Round, anchor);
            foreach (var signal in d.Signals(round.Round))
            {
                // Сигнал учитывается, если игрок в этот момент ещё был на позиции.
                if (signal.Time < 10 || leave is { } l && l.Time < signal.Time)
                    continue;

                var key = signal.Name;
                if (!bySignal.TryGetValue(key, out var entry))
                    entry = (0, []);
                entry.Occurred++;
                if (leave is { } left && left.Time - signal.Time <= RotationSignalWindow + 7)
                    entry.Left.Add((round.Round, anchor, left.Via, left.Time));
                bySignal[key] = entry;
                break; // первый сигнал раунда
            }
        }

        foreach (var (signal, (occurred, left)) in bySignal)
        {
            if (left.Count == 0)
                continue;
            var from = MostCommon(left.Select(l => l.From));
            var via = MostCommon(left.Select(l => l.Via));
            var time = Labels.Time(PatternStats.Median(left.Select(l => l.Time)));
            output.Add(d.Make(PatternCategory.Rotation, Side.CT, $"CT · {signal}", Loc.T($"уходит с {from}", $"leaves {from}"),
                Loc.T($"На CT в {{0}} из {{1}} раундов при сигнале «{signal}» уходит с {from}{(via.Length > 0 ? $" через {via}" : "")} на ~{time}",
                    $"On CT: in {{0}} of {{1}} rounds, on \"{signal}\", leaves {from}{(via.Length > 0 ? $" via {via}" : "")} at ~{time}"),
                left.Select(l => l.Round), occurred));
        }
    }

    // ---------- Реакции на ситуацию ----------

    private static void ReactionPatterns(ProfileData d, Side side, List<Pattern> output)
    {
        var rounds = d.SideRounds(side);
        var trade = new List<int>();
        var tradeTotal = 0;
        var advantage = (Total: 0, Contact: new List<int>(), Calm: new List<int>());
        var disadvantage = (Total: 0, Contact: new List<int>(), Calm: new List<int>());
        var clutch = (Total: 0, Hunt: new List<int>(), Save: new List<int>());
        var postPlant = new List<(int Round, string Zone)>();
        var retake = (Total: 0, Contact: new List<int>());

        foreach (var round in rounds)
        {
            var r = round.Round;
            var info = d.Match.FindRound(r)!;

            // После первой смерти в своей команде (игрок жив): идёт ли на размен в течение 8 с.
            if (d.FirstTeamDeath(r, side) is { } teamDeath && d.Context.IsAlive(d.SteamId, r, teamDeath.Tick))
            {
                tradeTotal++;
                if (d.HasContactBetween(r, teamDeath.Tick, teamDeath.Tick + (int)(TradeWindow * d.Match.TickRate)))
                    trade.Add(r);
            }

            // После первого убийства раунда — численное преимущество или меньшинство.
            if (d.Match.Kills.Where(k => k.Round == r).OrderBy(k => k.Tick).FirstOrDefault() is { } first &&
                d.Context.IsAlive(d.SteamId, r, first.Tick))
            {
                var ours = d.Context.AliveCount(r, side, first.Tick);
                var theirs = d.Context.AliveCount(r, side.Opposite(), first.Tick);
                var contact = d.HasContactBetween(r, first.Tick + 1, first.Tick + 10 * d.Match.TickRate);
                if (ours > theirs)
                {
                    advantage.Total++;
                    (contact ? advantage.Contact : advantage.Calm).Add(r);
                }
                else if (ours < theirs)
                {
                    disadvantage.Total++;
                    (contact ? disadvantage.Contact : disadvantage.Calm).Add(r);
                }
            }

            // Клатч: игрок остался последним против хотя бы одного противника.
            if (d.ClutchStart(r, side) is { } clutchTick)
            {
                clutch.Total++;
                if (d.HasContactBetween(r, clutchTick + 1, info.EndTick))
                    clutch.Hunt.Add(r);
                else if (d.Context.IsAlive(d.SteamId, r, info.EndTick))
                    clutch.Save.Add(r);
            }

            // Пост-плант.
            if (d.Match.Bombs.FirstOrDefault(b => b.Round == r && b.Type == BombEventType.Planted) is { } plant &&
                d.Context.IsAlive(d.SteamId, r, plant.Tick))
            {
                if (side == Side.T && d.ZoneAt(r, plant.Tick + 10 * d.Match.TickRate) is { Length: > 0 } zone)
                    postPlant.Add((r, zone));
                if (side == Side.CT)
                {
                    retake.Total++;
                    if (d.HasContactBetween(r, plant.Tick, plant.Tick + 25 * d.Match.TickRate))
                        retake.Contact.Add(r);
                }
            }
        }

        var s = Labels.Side(side);
        var on = On(side);
        var afterDeath = Loc.T($"{s} · смерть тиммейта", $"{s} · teammate death");
        output.Add(d.Make(PatternCategory.Reaction, side, afterDeath, Loc.T("идёт на размен", "goes for the trade"),
            Loc.T($"{on} после первой смерти в команде в {{0}} из {{1}} раундов в течение 8 с вступает в бой (размен)",
                $"{on}: after the first teammate death, joins the fight within 8 s (trade) in {{0}} of {{1}} rounds"),
            trade, tradeTotal));
        output.Add(d.Make(PatternCategory.Reaction, side, afterDeath, Loc.T("не меняется", "does not trade"),
            Loc.T($"{on} после первой смерти в команде в {{0}} из {{1}} раундов не вступает в бой в ближайшие 8 с",
                $"{on}: after the first teammate death, stays out of the fight for 8 s in {{0}} of {{1}} rounds"),
            rounds.Select(r => r.Round).Where(r => !trade.Contains(r) && d.FirstTeamDeath(r, side) is { } td && d.Context.IsAlive(d.SteamId, r, td.Tick)),
            tradeTotal));

        var seeks = Loc.T("ищет бой", "seeks a fight");
        var waits = Loc.T("выжидает", "waits");
        var ahead = Loc.T($"{s} · преимущество", $"{s} · man advantage");
        output.Add(d.Make(PatternCategory.Reaction, side, ahead, seeks,
            Loc.T($"{on} в численном преимуществе после первого фрага в {{0}} из {{1}} раундов сразу (≤10 с) ищет бой",
                $"{on}: up a player after the opening kill, seeks a fight right away (≤10 s) in {{0}} of {{1}} rounds"),
            advantage.Contact, advantage.Total));
        output.Add(d.Make(PatternCategory.Reaction, side, ahead, waits,
            Loc.T($"{on} в численном преимуществе после первого фрага в {{0}} из {{1}} раундов выжидает",
                $"{on}: up a player after the opening kill, waits in {{0}} of {{1}} rounds"),
            advantage.Calm, advantage.Total));
        var behind = Loc.T($"{s} · меньшинство", $"{s} · man disadvantage");
        output.Add(d.Make(PatternCategory.Reaction, side, behind, seeks,
            Loc.T($"{on} в численном меньшинстве после первого фрага в {{0}} из {{1}} раундов сразу ищет бой",
                $"{on}: down a player after the opening kill, seeks a fight right away in {{0}} of {{1}} rounds"),
            disadvantage.Contact, disadvantage.Total));
        output.Add(d.Make(PatternCategory.Reaction, side, behind, waits,
            Loc.T($"{on} в численном меньшинстве после первого фрага в {{0}} из {{1}} раундов выжидает",
                $"{on}: down a player after the opening kill, waits in {{0}} of {{1}} rounds"),
            disadvantage.Calm, disadvantage.Total));
        var clutchCondition = Loc.T($"{s} · клатч", $"{s} · clutch");
        output.Add(d.Make(PatternCategory.Reaction, side, clutchCondition, Loc.T("играет на фраги", "plays for kills"),
            Loc.T($"{on} в клатчах в {{0}} из {{1}} раундов играет на фраги", $"{on}: in clutches plays for kills in {{0}} of {{1}} rounds"),
            clutch.Hunt, clutch.Total));
        output.Add(d.Make(PatternCategory.Reaction, side, clutchCondition, Loc.T("сохраняется", "saves"),
            Loc.T($"{on} в клатчах в {{0}} из {{1}} раундов сохраняется без боя", $"{on}: in clutches saves without a fight in {{0}} of {{1}} rounds"),
            clutch.Save, clutch.Total));

        foreach (var group in postPlant.GroupBy(p => p.Zone))
        {
            output.Add(d.Make(PatternCategory.Reaction, side, Loc.T("T · пост-плант", "T · post-plant"),
                Loc.T($"позиция {group.Key}", $"position {group.Key}"),
                Loc.T($"На T после закладки в {{0}} из {{1}} раундов занимает {group.Key}", $"On T: after the plant takes {group.Key} in {{0}} of {{1}} rounds"),
                group.Select(p => p.Round), postPlant.Count));
        }
        output.Add(d.Make(PatternCategory.Reaction, side, Loc.T("CT · пост-плант", "CT · post-plant"), Loc.T("участвует в ретейке", "joins the retake"),
            Loc.T("На CT после закладки в {0} из {1} раундов участвует в ретейке (бой в течение 25 с)",
                "On CT: after the plant joins the retake (fights within 25 s) in {0} of {1} rounds"),
            retake.Contact, retake.Total));
    }

    // ---------- Гранаты ----------

    private static void GrenadePatterns(ProfileData d, Side side, List<Pattern> output)
    {
        var sideRounds = d.SideRounds(side).Count;
        var s = Labels.Side(side);
        var on = On(side);

        foreach (var cluster in d.Grenades.Clusters.Where(c => c.Side == side))
        {
            var (from, to, time) = (Zone(cluster.FromZone), Zone(cluster.ToZone), Labels.Time(cluster.MedianThrowTime));
            output.Add(d.Make(PatternCategory.Grenades, side, s, $"{Labels.Grenade(cluster.Type)} {cluster.FromZone} → {cluster.ToZone}",
                Loc.T($"{on} в {{0}} из {{1}} раундов бросает {GrenadeName(cluster.Type).ToLowerInvariant()} из {from} в {to} на ~{time}",
                    $"{on}: in {{0}} of {{1}} rounds throws {GrenadeName(cluster.Type)} from {from} to {to} at ~{time}"),
                cluster.Rounds, sideRounds, cluster.Throws.Select(t => t.Id)));
        }

        var throws = d.Grenades.Throws.Where(t => t.ThrowerSide == side).ToList();
        if (throws.Count > 0)
        {
            if (side == Side.T)
            {
                var beforeExit = throws.Where(t => d.TeamFirstContactTick(t.Round, side) is not { } c || t.ThrowTick <= c).ToList();
                output.Add(d.Make(PatternCategory.Grenades, side, Loc.T("T · гранаты", "T · grenades"), Loc.T("до выхода", "before the take"),
                    Loc.T("На T бросает гранаты перед выходом (до первого контакта команды): {0} из {1} гранат",
                        "On T: throws grenades before the take (before the first team contact): {0} of {1} grenades"),
                    beforeExit.Select(t => t.Round), throws.Count, beforeExit.Select(t => t.Id), countItems: beforeExit.Count));
            }
            else
            {
                // Сравнение «защита / ретейк» имеет смысл только в раундах, где бомбу заложили.
                var plants = d.Match.Bombs.Where(b => b.Type == BombEventType.Planted).ToDictionary(b => b.Round, b => b.Tick);
                var planted = throws.Where(t => plants.ContainsKey(t.Round)).ToList();
                var retake = planted.Where(t => t.ThrowTick > plants[t.Round]).ToList();
                var hold = planted.Except(retake).ToList();
                var condition = Loc.T("CT · раунды с закладкой", "CT · rounds with a plant");
                output.Add(d.Make(PatternCategory.Grenades, side, condition, Loc.T("гранаты в ретейке", "grenades in the retake"),
                    Loc.T("На CT в раундах с закладкой бросает гранаты в ретейке (после закладки): {0} из {1} гранат",
                        "On CT (rounds with a plant): throws grenades in the retake (after the plant): {0} of {1} grenades"),
                    retake.Select(t => t.Round), planted.Count, retake.Select(t => t.Id), countItems: retake.Count));
                output.Add(d.Make(PatternCategory.Grenades, side, condition, Loc.T("гранаты в защите", "grenades on defense"),
                    Loc.T("На CT в раундах с закладкой бросает гранаты до закладки (защита точки): {0} из {1} гранат",
                        "On CT (rounds with a plant): throws grenades before the plant (site defense): {0} of {1} grenades"),
                    hold.Select(t => t.Round), planted.Count, hold.Select(t => t.Id), countItems: hold.Count));
            }
        }

        var flashes = throws.Where(t => t.Type == GrenadeType.Flashbang && t.DetonateTick is not null).ToList();
        if (flashes.Count > 0)
        {
            var window = (int)(FlashContactWindow * d.Match.TickRate);
            var self = flashes.Where(f => d.HasContactBetween(f.Round, f.DetonateTick!.Value, f.DetonateTick.Value + window)).ToList();
            var mate = flashes.Except(self)
                .Where(f => d.TeamContactBetween(f.Round, side, f.DetonateTick!.Value, f.DetonateTick.Value + window)).ToList();
            var condition = Loc.T($"{s} · флешки", $"{s} · flashes");
            output.Add(d.Make(PatternCategory.Grenades, side, condition, Loc.T("под себя", "for own peek"),
                Loc.T($"{on} флешки бросает под себя (сам вступает в бой в течение 3 с): {{0}} из {{1}}",
                    $"{on}: flashes for own peek (fights within 3 s): {{0}} of {{1}}"),
                self.Select(f => f.Round), flashes.Count, self.Select(f => f.Id), countItems: self.Count));
            output.Add(d.Make(PatternCategory.Grenades, side, condition, Loc.T("под тиммейта", "for a teammate"),
                Loc.T($"{on} флешки бросает под тиммейта (в бой вступает союзник): {{0}} из {{1}}",
                    $"{on}: flashes for a teammate (a teammate takes the fight): {{0}} of {{1}}"),
                mate.Select(f => f.Round), flashes.Count, mate.Select(f => f.Id), countItems: mate.Count));
        }
    }

    // ---------- Места смертей ----------

    /// <summary>Повторяющиеся места смертей: «погибает в X в N из M раундов, когда туда заходит».</summary>
    private static void DeathPatterns(ProfileData d, Side side, List<Pattern> output)
    {
        var routesBySide = d.Routes.Rounds.Where(r => r.Side == side).ToList();
        var deaths = routesBySide.Where(r => r.Died && r.Zones.Count > 0).Select(r => (r.Round, Zone: r.Zones[^1].Zone)).ToList();
        var on = On(side);

        foreach (var group in deaths.GroupBy(x => x.Zone))
        {
            var visited = routesBySide.Count(r => r.ZoneNames.Contains(group.Key));
            output.Add(d.Make(PatternCategory.Deaths, side,
                Loc.T($"{Labels.Side(side)} · заходит в {group.Key}", $"{Labels.Side(side)} · enters {group.Key}"),
                Loc.T($"погибает в {group.Key}", $"dies in {group.Key}"),
                Loc.T($"{on} погибает в {group.Key} в {{0}} из {{1}} раундов, когда туда заходит",
                    $"{on}: dies in {group.Key} in {{0}} of {{1}} rounds when entering it"),
                group.Select(x => x.Round), visited));
        }
    }

    // ---------- Роль и предсказуемость ----------

    private static RoleAssessment ClassifyRole(ProfileData d, SideMetrics me)
    {
        var team = d.Teammates(me.Side).Select(id => d.Metrics(id, me.Side)).Where(m => m.Rounds > 0).Append(me).ToList();
        bool IsMax(Func<SideMetrics, double> f) => team.All(m => f(me) >= f(m));
        int Rank(Func<SideMetrics, double> f) => team.Count(m => f(m) > f(me)) + 1;
        var culture = Loc.Culture;
        string F(FormattableString s) => s.ToString(culture);

        var reasons = new List<string>
        {
            Loc.T(F($"первая дуэль раунда: {me.FirstDuelShare:P0} раундов (место {Rank(m => m.FirstDuelShare)} в команде)"),
                F($"opening duel: {me.FirstDuelShare:P0} of rounds (rank {Rank(m => m.FirstDuelShare)} in team)")),
            me.MedianContactTime is { } c
                ? Loc.T($"первый контакт ~{Labels.Time(c)}", $"first contact ~{Labels.Time(c)}")
                : Loc.T("контактов почти нет", "almost no contact"),
            Loc.T(F($"до ближайшего союзника ~{me.MedianTeammateDistance:F0} units (место {Rank(m => m.MedianTeammateDistance)})"),
                F($"nearest teammate ~{me.MedianTeammateDistance:F0} units away (rank {Rank(m => m.MedianTeammateDistance)})")),
            Loc.T(F($"гранат за раунд: {me.GrenadesPerRound:F1} (место {Rank(m => m.GrenadesPerRound)})"),
                F($"grenades per round: {me.GrenadesPerRound:F1} (rank {Rank(m => m.GrenadesPerRound)})")),
            Loc.T(F($"урон за раунд: {me.DamagePerRound:F0} (место {Rank(m => m.DamagePerRound)})"),
                F($"damage per round: {me.DamagePerRound:F0} (rank {Rank(m => m.DamagePerRound)})")),
        };
        if (me.Side == Side.CT)
            reasons.Add(Loc.T(F($"держит одну позицию в {me.AnchorShare:P0} раундов"), F($"holds one position in {me.AnchorShare:P0} of rounds")));

        PlayerRole role;
        if (IsMax(m => m.FirstDuelShare) && me.FirstDuelShare >= 0.25)
            role = PlayerRole.Entry;
        else if (me.Side == Side.T && IsMax(m => m.MedianTeammateDistance) && me.MedianTeammateDistance >= 900)
            role = PlayerRole.Lurker;
        else if (me.Side == Side.CT && me.AnchorShare >= 0.6)
            role = PlayerRole.Anchor;
        else if (me.Side == Side.CT && me.AnchorShare < 0.4)
            role = PlayerRole.Rotator;
        else if (Rank(m => m.GrenadesPerRound) <= 2 && Rank(m => m.FirstDuelShare) >= 3)
            role = PlayerRole.Support;
        else
            role = PlayerRole.Rifler;

        var confidence = me.Rounds < 5 ? Confidence.Low : me.Rounds < 9 ? Confidence.Medium : Confidence.High;
        return new RoleAssessment(me.Side, role, confidence, reasons);
    }

    /// <summary>
    /// Предсказуемость 0–100%: в группах похожих ситуаций (сторона + закупка команды, ≥2 раундов) доля самого
    /// частого решения — по маршруту, стартовой позиции, итоговой позиции и агрессии; среднее, взвешенное по размеру групп.
    /// </summary>
    internal static int Predictability(IReadOnlyList<RoundSituation> rounds)
    {
        Func<RoundSituation, string>[] decisions =
        [
            r => r.RouteKey,
            r => r.FinalZone,
            r => r.Aggression.ToString(),
        ];

        var groups = rounds.GroupBy(r => (r.Side, r.TeamBuy)).Where(g => g.Count() >= 2).ToList();
        if (groups.Count == 0)
            return 0;

        double weighted = 0, weight = 0;
        foreach (var group in groups)
        {
            var n = group.Count();
            var shares = decisions.Select(f => group.GroupBy(f).Max(g => g.Count()) / (double)n);
            weighted += shares.Average() * n;
            weight += n;
        }
        return (int)Math.Round(weighted / weight * 100);
    }

    // ---------- Подписи ----------

    private static bool IsSpawn(string zone) => zone.Contains("Spawn", StringComparison.OrdinalIgnoreCase);

    private static string Zone(string zone) => zone.Length > 0 ? zone : "?";

    private static string MostCommon(IEnumerable<string> values) =>
        values.Where(v => v.Length > 0).GroupBy(v => v).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault() ?? "";

    private static string AggressionText(Aggression a) => a switch
    {
        Aggression.Aggressive => Loc.T("агрессивно (первый бой в начале раунда)", "aggressively (first fight early in the round)"),
        Aggression.Passive => Loc.T("пассивно (бой поздно или без боя)", "passively (late fight or no fight)"),
        _ => Loc.T("сдержанно (первый бой в середине раунда)", "with restraint (first fight mid-round)"),
    };

    private static string OnScore(Side side, ScoreState s) => s switch
    {
        ScoreState.Leading => On(side, "когда команда ведёт в счёте", "team leading"),
        ScoreState.Trailing => On(side, "когда команда отстаёт в счёте", "team trailing"),
        _ => On(side, "при равном счёте", "score tied"),
    };

    /// <summary>Название гранаты в винительном падеже («бросает флешку») / с артиклем («throws a flashbang»).</summary>
    private static string GrenadeName(GrenadeType type) => type switch
    {
        GrenadeType.HighExplosive => Loc.T("HE", "an HE"),
        GrenadeType.Flashbang => Loc.T("Флешку", "a flashbang"),
        GrenadeType.Smoke => Loc.T("Дым", "a smoke"),
        GrenadeType.Molotov => Loc.T("Молотов", "a molotov"),
        GrenadeType.Incendiary => Loc.T("Зажигательную", "an incendiary"),
        _ => Loc.T("Ложную", "a decoy"),
    };

    /// <summary>Предрасчитанные данные игрока и матча для генераторов паттернов.</summary>
    private sealed class ProfileData
    {
        private readonly IRoundContextService _contexts;
        private readonly ISituationAnalysisService _situations;
        private readonly PatternOptions _options;
        private readonly Dictionary<string, Vector3> _zoneCentroids;
        private readonly Dictionary<(ulong, Side), SideMetrics> _metrics = [];

        public ProfileData(MatchData match, ulong steamId, PatternOptions options, ISituationAnalysisService situations,
            IRouteAnalysisService routes, IGrenadeAnalysisService grenades, IRoundContextService contacts)
        {
            Match = match;
            SteamId = steamId;
            _options = options;
            _contexts = contacts;
            _situations = situations;
            Context = contacts.Create(match, options.Economy);
            Situations = situations.Analyze(match, steamId, SituationOptions()).Rounds;
            Routes = routes.Analyze(match, steamId);
            Grenades = grenades.Analyze(match, steamId,
                new GrenadeClusterOptions(options.GrenadeClusterThrowDistance, options.GrenadeClusterLandDistance));
            _zoneCentroids = match.Tracks.Values
                .SelectMany(t => t.Samples)
                .Where(s => s.Place.Length > 0)
                .GroupBy(s => s.Place)
                .ToDictionary(g => g.Key, g => g.Aggregate(Vector3.Zero, (a, s) => a + s.Position) / g.Count());
            Zones = new ZoneLocator(match);
        }

        public MatchData Match { get; }
        public ulong SteamId { get; }
        public IMatchContext Context { get; }
        public IReadOnlyList<RoundSituation> Situations { get; }
        public RouteAnalysis Routes { get; }
        public GrenadeAnalysis Grenades { get; }
        public ZoneLocator Zones { get; }

        public IReadOnlyList<RoundSituation> SideRounds(Side side) => Situations.Where(s => s.Side == side).ToList();

        public Pattern Make(PatternCategory category, Side side, string condition, string action, string textTemplate,
            IEnumerable<int> rounds, int total, IEnumerable<int>? grenadeIds = null, int? countItems = null)
        {
            var roundList = rounds.Distinct().Order().ToList();
            var count = countItems ?? roundList.Count;
            var wilson = PatternStats.WilsonLowerBound(count, total);
            var text = string.Format(textTemplate, count, total);
            return new Pattern(category, side, condition, action, text, count, total, wilson, PatternStats.Level(wilson),
                roundList, grenadeIds?.ToList() ?? []);
        }

        private SituationOptions SituationOptions() =>
            new(_options.Economy, _options.AggressiveContactSeconds, _options.PassiveContactSeconds);

        public IEnumerable<ulong> Teammates(Side side)
        {
            var myRounds = Situations.Where(s => s.Side == side).Select(s => s.Round).ToHashSet();
            return Match.Players
                .Where(p => p.SteamId != SteamId && myRounds.Count(r => p.SideInRound(r) == side) * 2 >= myRounds.Count)
                .Select(p => p.SteamId);
        }

        /// <summary>Зона, которую игрок держал большую часть первых 40 с (не спавн), или null.</summary>
        public string? AnchorZone(int round) => AnchorZone(SteamId, round);

        private string? AnchorZone(ulong player, int round)
        {
            var early = Match.TrackOf(player).InRound(round).Where(s => s.TimeInRound <= 40 && s.Place.Length > 0).ToList();
            if (early.Count < 8)
                return null;
            var top = early.Where(s => !IsSpawn(s.Place)).GroupBy(s => s.Place).OrderByDescending(g => g.Count()).FirstOrDefault();
            // Доля считается без времени на спавне: оттуда все выходят в начале раунда.
            var outsideSpawn = early.Count(s => !IsSpawn(s.Place));
            return top is not null && top.Count() >= outsideSpawn * 0.5 ? top.Key : null;
        }

        /// <summary>Момент ухода с позиции: игрок дальше 900 units от центра зоны и не возвращается ≥5 с.</summary>
        public (float Time, string Via)? LeaveTime(int round, string zone)
        {
            if (!_zoneCentroids.TryGetValue(zone, out var center))
                return null;
            var samples = Match.TrackOf(SteamId).InRound(round).Where(s => s.TimeInRound >= 15).ToList();
            for (var i = 0; i < samples.Count; i++)
            {
                if (Distance2D(samples[i].Position, center) <= 900)
                    continue;
                var holdUntil = samples[i].TimeInRound + 5;
                var stays = samples.Skip(i).TakeWhile(s => s.TimeInRound <= holdUntil).All(s => Distance2D(s.Position, center) > 900);
                if (!stays)
                    continue;
                var via = samples.Skip(i).Select(s => s.Place).FirstOrDefault(p => p.Length > 0 && p != zone) ?? "";
                return (samples[i].TimeInRound, via);
            }
            return null;
        }

        /// <summary>События-сигналы для ротации в хронологическом порядке.</summary>
        public IEnumerable<(string Name, float Time)> Signals(int round)
        {
            var info = Match.FindRound(round)!;
            float Time(int tick) => (tick - info.FreezeEndTick) / (float)Match.TickRate;
            var side = Situations.First(s => s.Round == round).Side;
            var events = new List<(string, float)>();

            if (Match.Bombs.FirstOrDefault(b => b.Round == round && b.Type == BombEventType.Planted) is { } plant)
                events.Add((Loc.T($"закладка на {plant.Site}", $"plant on {plant.Site}"), Time(plant.Tick)));
            if (FirstTeamDeath(round, side) is { } death)
            {
                var zone = Zones.Locate(death.VictimPosition);
                var where = zone.Length > 0 ? $" ({zone})" : "";
                events.Add((Loc.T($"смерть тиммейта{where}", $"teammate death{where}"), Time(death.Tick)));
            }
            if (TeamFirstContactTick(round, side) is { } contact)
                events.Add((Loc.T("контакт команды", "team contact"), Time(contact)));

            return events.OrderBy(e => e.Item2);
        }

        public KillEvent? FirstTeamDeath(int round, Side side) =>
            Match.Kills.Where(k => k.Round == round && k.VictimSteamId != SteamId &&
                                   Match.FindPlayer(k.VictimSteamId)?.SideInRound(round) == side)
                .OrderBy(k => k.Tick)
                .FirstOrDefault();

        /// <summary>Тик, когда игрок остался последним живым в команде при живых противниках.</summary>
        public int? ClutchStart(int round, Side side)
        {
            foreach (var kill in Match.Kills.Where(k => k.Round == round).OrderBy(k => k.Tick))
            {
                if (!Context.IsAlive(SteamId, round, kill.Tick))
                    return null;
                if (Context.AliveCount(round, side, kill.Tick) == 1 && Context.AliveCount(round, side.Opposite(), kill.Tick) >= 1)
                    return kill.Tick;
            }
            return null;
        }

        public bool HasContactBetween(int round, int fromTick, int toTick) => ContactBetween(round, SteamId, fromTick, toTick);

        public bool TeamContactBetween(int round, Side side, int fromTick, int toTick) =>
            Match.Players.Where(p => p.SteamId != SteamId && p.SideInRound(round) == side)
                .Any(p => ContactBetween(round, p.SteamId, fromTick, toTick));

        private bool ContactBetween(int round, ulong player, int fromTick, int toTick) =>
            Match.Damages.Any(d => d.Round == round && d.Tick >= fromTick && d.Tick <= toTick && d.AttackerSteamId is not null &&
                                   d.AttackerSteamId != d.VictimSteamId && (d.AttackerSteamId == player || d.VictimSteamId == player)) ||
            Match.Kills.Any(k => k.Round == round && k.Tick >= fromTick && k.Tick <= toTick &&
                                 (k.AttackerSteamId == player || k.VictimSteamId == player));

        public int? TeamFirstContactTick(int round, Side side)
        {
            var team = Match.Players.Where(p => p.SideInRound(round) == side).Select(p => p.SteamId).ToHashSet();
            return Match.Damages
                .Where(d => d.Round == round && d.AttackerSteamId is { } a && a != d.VictimSteamId &&
                            (team.Contains(a) || team.Contains(d.VictimSteamId)))
                .Select(d => (int?)d.Tick)
                .Min();
        }

        public string? ZoneAt(int round, int tick) =>
            Match.TrackOf(SteamId).InRound(round).LastOrDefault(s => s.Tick <= tick).Place;

        public SideMetrics Metrics(ulong player, Side side)
        {
            if (_metrics.TryGetValue((player, side), out var cached))
                return cached;

            var rounds = (player == SteamId ? Situations : _situations.Analyze(Match, player, SituationOptions()).Rounds)
                .Where(r => r.Side == side)
                .ToList();
            var n = Math.Max(1, rounds.Count);
            var contacts = rounds.Where(r => r.FirstContactTime is not null).Select(r => r.FirstContactTime!.Value).ToList();
            var duels = rounds.Count(r => r.FirstDuel != FirstDuel.NotInvolved);

            var metrics = new SideMetrics(
                side,
                rounds.Count,
                rounds.Sum(r => r.Kills),
                rounds.Count(r => r.Died),
                rounds.Count(r => !r.Died) / (double)n,
                rounds.Count(r => r.RoundWon) / (double)n,
                contacts.Count > 0 ? PatternStats.Median(contacts) : null,
                contacts.Count / (double)n,
                duels / (double)n,
                duels == 0 ? 0 : rounds.Count(r => r.FirstDuel == FirstDuel.Won) / (double)duels,
                MedianTeammateDistance(player, rounds.Select(r => r.Round), side),
                rounds.Sum(r => r.GrenadeCount) / (double)n,
                rounds.Sum(r => r.Damage) / (double)n,
                rounds.Count(r => AnchorZone(player, r.Round) is not null) / (double)n,
                rounds.Count(r => r.Aggression == Aggression.Aggressive) / (double)n,
                rounds.Count(r => r.Aggression == Aggression.Passive) / (double)n);
            _metrics[(player, side)] = metrics;
            return metrics;
        }

        /// <summary>Медианное расстояние до ближайшего живого союзника на 10–40 с раунда (сэмплы всех игроков синхронны по тикам).</summary>
        private float MedianTeammateDistance(ulong player, IEnumerable<int> rounds, Side side)
        {
            var distances = new List<float>();
            foreach (var round in rounds)
            {
                var mates = Match.Players
                    .Where(p => p.SteamId != player && p.SideInRound(round) == side)
                    .Select(p => Match.TrackOf(p.SteamId).InRound(round).ToDictionary(s => s.Tick, s => s.Position))
                    .ToList();
                foreach (var sample in Match.TrackOf(player).InRound(round).Where(s => s.TimeInRound is >= 10 and <= 40))
                {
                    var nearest = mates
                        .Select(m => m.TryGetValue(sample.Tick, out var pos) ? Distance2D(pos, sample.Position) : float.MaxValue)
                        .DefaultIfEmpty(float.MaxValue)
                        .Min();
                    if (nearest < float.MaxValue)
                        distances.Add(nearest);
                }
            }
            return PatternStats.Median(distances);
        }

        private static float Distance2D(Vector3 a, Vector3 b) => Vector2.Distance(new Vector2(a.X, a.Y), new Vector2(b.X, b.Y));
    }
}
