using System.Numerics;
using DemReview.Application.Damage;
using DemReview.Domain;
using DemReview.Tests.Context;

namespace DemReview.Tests.Damage;

public class DamageAnalysisTests
{
    /// <summary>Игроки 1–5 — T, 6–10 — CT.</summary>
    private static MatchData Match(IReadOnlyList<DamageEvent> damages, IReadOnlyList<GrenadeThrow>? grenades = null) =>
        new SyntheticMatch().FiveVsFive().Rounds(2).Build() with { Damages = damages, Grenades = grenades ?? [] };

    private static DamageEvent Hit(int round, float seconds, ulong? attacker, ulong victim, int damage, string weapon = "ak47") =>
        new(SyntheticMatch.At(round, seconds), round, attacker, victim, damage, weapon);

    private static GrenadeThrow Grenade(int id, GrenadeType type, ulong thrower, int round, float detonateSeconds) =>
        new(id, type, thrower, Side.T, round, SyntheticMatch.At(round, detonateSeconds - 1.5f), detonateSeconds - 1.5f,
            Vector3.Zero, 0, 0, "", Vector3.Zero, SyntheticMatch.At(round, detonateSeconds), Vector3.One, []);

    [Fact]
    public void Damage_is_capped_by_remaining_health_per_round()
    {
        // 80 + 60 по одной жертве: фактически 80 + 20. В следующем раунде здоровье снова 100.
        var match = Match([Hit(1, 10, 1, 6, 80), Hit(1, 12, 2, 6, 60, "awp"), Hit(2, 5, 1, 6, 120, "awp")]);

        var hits = DamageAnalysis.EffectiveHits(match);

        Assert.Equal([80, 20, 100], hits.Select(h => h.Damage));
        Assert.Equal([80, 60, 120], hits.Select(h => h.RawDamage));
    }

    [Fact]
    public void Teammate_and_self_damage_are_marked()
    {
        var match = Match([Hit(1, 10, 1, 2, 10, "hegrenade"), Hit(1, 11, 1, 1, 5, "hegrenade"), Hit(1, 12, 1, 6, 30)]);

        var hits = DamageAnalysis.EffectiveHits(match);

        Assert.Equal([true, true, false], hits.Select(h => h.IsTeammate));
        Assert.Equal(30, DamageAnalysis.DealtByRound(hits, 1)[1]);
    }

    [Fact]
    public void He_damage_is_linked_to_its_detonation()
    {
        var match = Match(
            [Hit(1, 20, 1, 6, 40, "hegrenade"), Hit(1, 20, 1, 7, 25, "hegrenade"), Hit(1, 20, 1, 2, 8, "hegrenade"), Hit(1, 35, 1, 8, 50, "hegrenade")],
            [Grenade(1, GrenadeType.HighExplosive, 1, 1, 20), Grenade(2, GrenadeType.HighExplosive, 1, 1, 35)]);

        var byGrenade = DamageAnalysis.ByGrenade(match, DamageAnalysis.EffectiveHits(match));

        Assert.Equal(65, byGrenade[1].EnemyDamage);
        Assert.Equal(2, byGrenade[1].EnemiesHit);
        Assert.Equal(8, byGrenade[1].TeamDamage);
        Assert.Equal(50, byGrenade[2].EnemyDamage);
    }

    [Fact]
    public void Fire_damage_is_collected_for_eight_seconds_after_ignition()
    {
        var match = Match(
            [Hit(1, 30.5f, 1, 6, 8, "inferno"), Hit(1, 33, 1, 6, 8, "inferno"), Hit(1, 37.5f, 1, 7, 8, "inferno"), Hit(1, 45, 1, 6, 8, "inferno")],
            [Grenade(1, GrenadeType.Molotov, 1, 1, 30)]);

        var fire = DamageAnalysis.ByGrenade(match, DamageAnalysis.EffectiveHits(match))[1];

        Assert.Equal(24, fire.EnemyDamage); // попадание на 45 с — уже не от этого огня
        Assert.Equal(2, fire.EnemiesHit);
    }
}
