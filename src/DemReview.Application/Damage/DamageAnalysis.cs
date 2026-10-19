using DemReview.Domain;

namespace DemReview.Application.Damage;

/// <summary>Попадание с фактическим уроном по здоровью.</summary>
/// <param name="Damage">Урон, ограниченный оставшимся здоровьем жертвы (как в табло CS2).</param>
/// <param name="RawDamage">Урон из события player_hurt (может превышать оставшееся здоровье).</param>
/// <param name="IsTeammate">Урон по союзнику (или по себе).</param>
public sealed record DamageHit(
    int Tick,
    int Round,
    ulong? AttackerSteamId,
    ulong VictimSteamId,
    int Damage,
    int RawDamage,
    string Weapon,
    bool IsTeammate);

/// <summary>Урон от одной гранаты (HE — взрыв, молотов/зажигательная — огонь).</summary>
public sealed record GrenadeDamage(int GrenadeId, IReadOnlyList<DamageHit> Hits)
{
    public int EnemyDamage => Hits.Where(h => !h.IsTeammate).Sum(h => h.Damage);

    public int TeamDamage => Hits.Where(h => h.IsTeammate).Sum(h => h.Damage);

    public int EnemiesHit => Hits.Where(h => !h.IsTeammate).Select(h => h.VictimSteamId).Distinct().Count();

    public int TeammatesHit => Hits.Where(h => h.IsTeammate).Select(h => h.VictimSteamId).Distinct().Count();
}

/// <summary>
/// Урон в матче: player_hurt пересчитывается в фактический урон (не больше оставшегося здоровья — каждый раунд
/// игрок начинает со 100 HP), гранатный урон привязывается к конкретным броскам.
/// </summary>
public static class DamageAnalysis
{
    /// <summary>Сколько после возгорания ещё может идти урон от огня этой гранаты, секунды.</summary>
    private const float FireDamageSeconds = 8;

    /// <summary>Допуск между тиком детонации HE и тиком урона.</summary>
    private const int HeTickTolerance = 4;

    public static IReadOnlyList<DamageHit> EffectiveHits(MatchData match)
    {
        var health = new Dictionary<(int Round, ulong Victim), int>();
        var hits = new List<DamageHit>(match.Damages.Count);
        foreach (var d in match.Damages.OrderBy(d => d.Tick))
        {
            var key = (d.Round, d.VictimSteamId);
            var remaining = health.GetValueOrDefault(key, 100);
            var damage = Math.Clamp(d.Damage, 0, remaining);
            health[key] = remaining - damage;

            var victimSide = match.FindPlayer(d.VictimSteamId)?.SideInRound(d.Round) ?? Side.None;
            var attackerSide = d.AttackerSteamId is { } a ? match.FindPlayer(a)?.SideInRound(d.Round) ?? Side.None : Side.None;
            var isTeammate = d.AttackerSteamId == d.VictimSteamId || (victimSide != Side.None && victimSide == attackerSide);
            hits.Add(new DamageHit(d.Tick, d.Round, d.AttackerSteamId, d.VictimSteamId, damage, d.Damage, d.Weapon, isTeammate));
        }
        return hits;
    }

    /// <summary>Нанесённый противникам урон игрока по раундам.</summary>
    public static IReadOnlyDictionary<int, int> DealtByRound(IEnumerable<DamageHit> hits, ulong steamId) =>
        hits.Where(h => h.AttackerSteamId == steamId && !h.IsTeammate)
            .GroupBy(h => h.Round)
            .ToDictionary(g => g.Key, g => g.Sum(h => h.Damage));

    /// <summary>Урон гранат: HE — попадания того же бросившего в тик взрыва, огонь — в течение ~8 с после возгорания.</summary>
    public static IReadOnlyDictionary<int, GrenadeDamage> ByGrenade(MatchData match, IReadOnlyList<DamageHit> hits)
    {
        var result = new Dictionary<int, List<DamageHit>>();
        var detonated = match.Grenades
            .Where(g => g.DetonateTick is not null && g.Type is GrenadeType.HighExplosive or GrenadeType.Molotov or GrenadeType.Incendiary)
            .GroupBy(g => (g.ThrowerSteamId, g.Round, IsFire: g.Type != GrenadeType.HighExplosive))
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.DetonateTick).ToList());

        var fireTicks = (int)(FireDamageSeconds * match.TickRate);
        foreach (var hit in hits)
        {
            if (hit.AttackerSteamId is not { } attacker || GrenadeKindOf(hit.Weapon) is not { } isFire ||
                !detonated.TryGetValue((attacker, hit.Round, isFire), out var candidates))
                continue;

            // Последняя граната, сработавшая до попадания (для HE — с небольшим допуском).
            var grenade = candidates.LastOrDefault(g => isFire
                ? g.DetonateTick <= hit.Tick && hit.Tick - g.DetonateTick <= fireTicks
                : Math.Abs(hit.Tick - g.DetonateTick!.Value) <= HeTickTolerance);
            if (grenade is null)
                continue;

            if (!result.TryGetValue(grenade.Id, out var list))
                result[grenade.Id] = list = [];
            list.Add(hit);
        }

        return result.ToDictionary(kv => kv.Key, kv => new GrenadeDamage(kv.Key, kv.Value));
    }

    /// <summary>true — урон огнём, false — HE, null — не гранатный урон.</summary>
    private static bool? GrenadeKindOf(string weapon) => weapon switch
    {
        "hegrenade" => false,
        "inferno" or "molotov" or "incgrenade" => true,
        _ => null,
    };
}
