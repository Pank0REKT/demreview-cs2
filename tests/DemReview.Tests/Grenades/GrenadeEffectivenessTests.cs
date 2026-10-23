using System.Numerics;
using DemReview.Application.Grenades;
using DemReview.Domain;
using DemReview.Tests.Context;

namespace DemReview.Tests.Grenades;

public class GrenadeEffectivenessTests
{
    /// <summary>Игроки 1–5 — T, 6–10 — CT.</summary>
    private static MatchData Match(IReadOnlyList<GrenadeThrow> grenades, IReadOnlyList<DamageEvent>? damages = null,
        IReadOnlyList<KillEvent>? kills = null) =>
        new SyntheticMatch().FiveVsFive().Rounds(2).Build() with
        {
            Grenades = grenades,
            Damages = damages ?? [],
            Kills = kills ?? [],
        };

    private static GrenadeThrow Grenade(int id, GrenadeType type, ulong thrower, float detonateSeconds, params FlashVictim[] victims) =>
        new(id, type, thrower, Side.T, 1, SyntheticMatch.At(1, detonateSeconds - 1.5f), detonateSeconds - 1.5f,
            Vector3.Zero, 0, 0, "", Vector3.Zero, SyntheticMatch.At(1, detonateSeconds), Vector3.One, victims);

    private static DamageEvent Hit(float seconds, ulong attacker, ulong victim, int damage, string weapon) =>
        new(SyntheticMatch.At(1, seconds), 1, attacker, victim, damage, weapon);

    private static KillEvent Kill(float seconds, ulong attacker, ulong victim, string weapon = "ak47") =>
        new(SyntheticMatch.At(1, seconds), 1, attacker, victim, null, weapon, false, null, Vector3.Zero);

    [Fact]
    public void He_and_fire_damage_count_enemies_and_teammates_separately()
    {
        var match = Match(
            [Grenade(1, GrenadeType.HighExplosive, 1, 10), Grenade(2, GrenadeType.Molotov, 1, 20)],
            [
                Hit(10, 1, 6, 40, "hegrenade"), Hit(10, 1, 7, 25, "hegrenade"), Hit(10, 1, 2, 10, "hegrenade"),
                Hit(21, 1, 8, 8, "inferno"), Hit(22, 1, 8, 8, "inferno"),
            ]);

        var result = GrenadeEffectivenessAnalysis.Analyze(match);

        var he = result.Outcomes[1];
        Assert.Equal(65, he.EnemyDamage);
        Assert.Equal(2, he.EnemiesHit);
        Assert.Equal(10, he.TeamDamage);
        Assert.True(he.PaidOff);
        Assert.True(he.HurtTeam);

        var stats = result.Of(1)!;
        Assert.Equal(1, stats.HeThrown);
        Assert.Equal(65, stats.HeDamage);
        Assert.Equal(1, stats.FireThrown);
        Assert.Equal(16, stats.FireDamage);
        Assert.Equal(10, stats.TeamDamage);
        Assert.Equal(65f, stats.HeDamagePerThrow);
    }

    [Fact]
    public void Flash_counts_blinded_enemies_teammates_and_self()
    {
        var flash = Grenade(1, GrenadeType.Flashbang, 1, 10,
            new FlashVictim(6, 2.5f, false, false), new FlashVictim(7, 1.0f, false, false),
            new FlashVictim(2, 1.5f, true, false), new FlashVictim(1, 0.5f, true, true));
        var useless = Grenade(2, GrenadeType.Flashbang, 1, 30, new FlashVictim(3, 2f, true, false));

        var result = GrenadeEffectivenessAnalysis.Analyze(Match([flash, useless]));

        var first = result.Outcomes[1];
        Assert.Equal(2, first.EnemiesBlinded);
        Assert.Equal(3.5f, first.EnemyBlindSeconds);
        Assert.Equal(1, first.TeammatesBlinded);
        Assert.True(first.SelfBlinded);
        Assert.False(result.Outcomes[2].PaidOff);
        Assert.True(result.Outcomes[2].HurtTeam);

        var stats = result.Of(1)!;
        Assert.Equal(2, stats.FlashesThrown);
        Assert.Equal(2, stats.EnemiesBlinded);
        Assert.Equal(2, stats.TeammatesBlinded);
        Assert.Equal(3.5f, stats.TeammateBlindSeconds);
        Assert.Equal(1f, stats.EnemiesPerFlash);
    }

    [Fact]
    public void Flash_kill_counts_only_while_the_enemy_is_still_blind()
    {
        // Флешка в 10 с: игрок 6 ослеплён на 3 с, игрок 7 — на 1 с.
        var flash = Grenade(1, GrenadeType.Flashbang, 1, 10, new FlashVictim(6, 3f, false, false), new FlashVictim(7, 1f, false, false));
        var match = Match([flash], kills:
        [
            Kill(12, 2, 6),  // тиммейт добил ослеплённого — засчитано
            Kill(13, 3, 7),  // ослепление уже прошло — не засчитано
        ]);

        var result = GrenadeEffectivenessAnalysis.Analyze(match);

        Assert.Equal(1, result.Outcomes[1].FlashKills);
        Assert.Equal(1, result.Of(1)!.FlashKills);
    }

    [Fact]
    public void Kill_of_a_flashed_enemy_by_the_enemy_team_is_not_a_flash_kill()
    {
        var flash = Grenade(1, GrenadeType.Flashbang, 1, 10, new FlashVictim(6, 3f, false, false));
        var match = Match([flash], kills: [Kill(11, 7, 6)]);

        Assert.Equal(0, GrenadeEffectivenessAnalysis.Analyze(match).Outcomes[1].FlashKills);
    }

    [Fact]
    public void He_kill_is_counted_for_the_grenade()
    {
        var match = Match(
            [Grenade(1, GrenadeType.HighExplosive, 1, 10)],
            [Hit(10, 1, 6, 100, "hegrenade")],
            [Kill(10, 1, 6, "hegrenade")]);

        var result = GrenadeEffectivenessAnalysis.Analyze(match);

        Assert.Equal(1, result.Outcomes[1].Kills);
        Assert.Equal(1, result.Of(1)!.GrenadeKills);
    }

    [Fact]
    public void Players_without_grenades_are_not_listed()
    {
        var match = Match([Grenade(1, GrenadeType.Smoke, 1, 10), Grenade(2, GrenadeType.Decoy, 6, 10)]);

        var result = GrenadeEffectivenessAnalysis.Analyze(match);

        Assert.Equal([1UL, 6UL], result.Players.Select(p => p.SteamId).Order());
        Assert.Equal(1, result.Of(1)!.SmokesThrown);
        Assert.Equal(1, result.Of(6)!.DecoysThrown);
        Assert.Null(result.Of(2));
    }
}
