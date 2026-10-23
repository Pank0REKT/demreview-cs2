using DemReview.Application.Damage;
using DemReview.Domain;

namespace DemReview.Application.Grenades;

/// <summary>Чем закончился один бросок: урон, ослеплённые и убийства, которые он помог сделать.</summary>
/// <param name="EnemiesBlinded">Ослеплённые флешкой противники.</param>
/// <param name="TeammatesBlinded">Ослеплённые союзники (без самого бросившего).</param>
/// <param name="FlashKills">Сколько ослеплённых этой флешкой противников убила команда бросившего, пока они не видели.</param>
public sealed record GrenadeOutcome(
    GrenadeThrow Throw,
    int EnemyDamage,
    int TeamDamage,
    int EnemiesHit,
    int Kills,
    int EnemiesBlinded,
    float EnemyBlindSeconds,
    int TeammatesBlinded,
    float TeammateBlindSeconds,
    bool SelfBlinded,
    int FlashKills)
{
    /// <summary>Граната принесла пользу: урон или ослепление противника.</summary>
    public bool PaidOff => EnemyDamage > 0 || EnemiesBlinded > 0;

    /// <summary>Граната задела своих: урон по союзникам или ослепление союзника.</summary>
    public bool HurtTeam => TeamDamage > 0 || TeammatesBlinded > 0;
}

/// <summary>Эффективность гранат одного игрока за матч.</summary>
/// <param name="HeDamage">Урон HE по противникам.</param>
/// <param name="FireDamage">Урон огнём (молотов и зажигательная) по противникам.</param>
/// <param name="TeamDamage">Урон HE и огнём по союзникам (и себе).</param>
/// <param name="GrenadeKills">Убийства HE и огнём.</param>
/// <param name="FlashKills">Убийства ослеплённых флешками игрока противников (им или командой).</param>
public sealed record PlayerGrenadeStats(
    ulong SteamId,
    int HeThrown,
    int HeDamage,
    int FireThrown,
    int FireDamage,
    int TeamDamage,
    int GrenadeKills,
    int FlashesThrown,
    int EnemiesBlinded,
    float EnemyBlindSeconds,
    int TeammatesBlinded,
    float TeammateBlindSeconds,
    int FlashKills,
    int SmokesThrown,
    int DecoysThrown)
{
    public int Thrown => HeThrown + FireThrown + FlashesThrown + SmokesThrown + DecoysThrown;

    public float HeDamagePerThrow => HeThrown == 0 ? 0 : HeDamage / (float)HeThrown;

    public float FireDamagePerThrow => FireThrown == 0 ? 0 : FireDamage / (float)FireThrown;

    public float EnemiesPerFlash => FlashesThrown == 0 ? 0 : EnemiesBlinded / (float)FlashesThrown;
}

/// <summary>Эффективность гранат в матче: по каждому игроку и по каждому броску.</summary>
public sealed record GrenadeEffectiveness(IReadOnlyList<PlayerGrenadeStats> Players, IReadOnlyDictionary<int, GrenadeOutcome> Outcomes)
{
    public PlayerGrenadeStats? Of(ulong steamId) => Players.FirstOrDefault(p => p.SteamId == steamId);

    public static GrenadeEffectiveness Empty { get; } = new([], new Dictionary<int, GrenadeOutcome>());
}

/// <summary>
/// Насколько полезны гранаты: урон HE и огня (фактический, как в табло), ослепления флешками противников
/// и союзников и убийства ослеплённых противников, пока длится ослепление.
/// </summary>
public static class GrenadeEffectivenessAnalysis
{
    public static GrenadeEffectiveness Analyze(MatchData match)
    {
        var hits = DamageAnalysis.EffectiveHits(match);
        var damage = DamageAnalysis.ByGrenade(match, hits);
        var outcomes = match.Grenades.ToDictionary(g => g.Id, g => Outcome(match, g, damage.GetValueOrDefault(g.Id)));

        var players = match.Players
            .Select(p => Stats(p.SteamId, match.GrenadesOf(p.SteamId).Select(g => outcomes[g.Id]).ToList()))
            .Where(s => s.Thrown > 0)
            .ToList();
        return new GrenadeEffectiveness(players, outcomes);
    }

    private static GrenadeOutcome Outcome(MatchData match, GrenadeThrow g, GrenadeDamage? damage)
    {
        var enemies = g.FlashVictims.Where(v => !v.IsTeammate).ToList();
        var mates = g.FlashVictims.Where(v => v.IsTeammate && !v.IsSelf).ToList();
        var kills = damage is null ? 0 : KillsBy(match, g, damage);
        return new GrenadeOutcome(
            g,
            damage?.EnemyDamage ?? 0,
            damage?.TeamDamage ?? 0,
            damage?.EnemiesHit ?? 0,
            kills,
            enemies.Count,
            enemies.Sum(v => v.Duration),
            mates.Count,
            mates.Sum(v => v.Duration),
            g.FlashVictims.Any(v => v.IsSelf),
            FlashKills(match, g, enemies));
    }

    /// <summary>Убийства гранатой: смертельное попадание этой гранаты совпадает по тику с убийством того же оружия.</summary>
    private static int KillsBy(MatchData match, GrenadeThrow g, GrenadeDamage damage) =>
        damage.Hits
            .Where(h => !h.IsTeammate)
            .Count(h => match.Kills.Any(k => k.Round == h.Round && k.Tick == h.Tick && k.VictimSteamId == h.VictimSteamId &&
                                             k.AttackerSteamId == g.ThrowerSteamId && IsGrenadeWeapon(k.Weapon)));

    /// <summary>Убийства ослеплённых противников командой бросившего, пока ослепление ещё не прошло.</summary>
    private static int FlashKills(MatchData match, GrenadeThrow g, IReadOnlyList<FlashVictim> enemies)
    {
        if (g.DetonateTick is not { } detonate || enemies.Count == 0)
            return 0;

        return enemies.Count(v => match.Kills.Any(k =>
            k.Round == g.Round && k.VictimSteamId == v.SteamId && k.Tick >= detonate &&
            k.Tick <= detonate + v.Duration * match.TickRate &&
            k.AttackerSteamId is { } attacker && attacker != v.SteamId &&
            match.FindPlayer(attacker)?.SideInRound(g.Round) == g.ThrowerSide));
    }

    private static PlayerGrenadeStats Stats(ulong steamId, IReadOnlyList<GrenadeOutcome> outcomes)
    {
        List<GrenadeOutcome> Of(params GrenadeType[] types) => outcomes.Where(o => types.Contains(o.Throw.Type)).ToList();
        var he = Of(GrenadeType.HighExplosive);
        var fire = Of(GrenadeType.Molotov, GrenadeType.Incendiary);
        var flashes = Of(GrenadeType.Flashbang);
        return new PlayerGrenadeStats(
            steamId,
            he.Count,
            he.Sum(o => o.EnemyDamage),
            fire.Count,
            fire.Sum(o => o.EnemyDamage),
            outcomes.Sum(o => o.TeamDamage),
            outcomes.Sum(o => o.Kills),
            flashes.Count,
            flashes.Sum(o => o.EnemiesBlinded),
            flashes.Sum(o => o.EnemyBlindSeconds),
            flashes.Sum(o => o.TeammatesBlinded),
            flashes.Sum(o => o.TeammateBlindSeconds),
            flashes.Sum(o => o.FlashKills),
            Of(GrenadeType.Smoke).Count,
            Of(GrenadeType.Decoy).Count);
    }

    private static bool IsGrenadeWeapon(string weapon) => weapon is "hegrenade" or "inferno" or "molotov" or "incgrenade";
}
