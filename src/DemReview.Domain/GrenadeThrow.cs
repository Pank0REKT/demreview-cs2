using System.Numerics;

namespace DemReview.Domain;

public enum GrenadeType
{
    HighExplosive,
    Flashbang,
    Smoke,
    Molotov,
    Incendiary,
    Decoy,
}

/// <summary>Ослеплённый флешкой игрок.</summary>
/// <param name="IsTeammate">Ослеплён союзник бросившего (или он сам).</param>
public sealed record FlashVictim(ulong SteamId, float Duration, bool IsTeammate, bool IsSelf);

/// <summary>Бросок гранаты и (если была) её детонация.</summary>
/// <param name="Id">Порядковый номер броска в матче.</param>
/// <param name="ThrowTime">Секунды от конца фризтайма.</param>
/// <param name="ThrowerPosition">Позиция игрока в момент броска.</param>
/// <param name="ProjectileStart">Точка появления снаряда.</param>
/// <param name="DetonatePosition">Точка детонации (для молотова — место возгорания); null — граната не сработала.</param>
public sealed record GrenadeThrow(
    int Id,
    GrenadeType Type,
    ulong ThrowerSteamId,
    Side ThrowerSide,
    int Round,
    int ThrowTick,
    float ThrowTime,
    Vector3 ThrowerPosition,
    float ThrowerPitch,
    float ThrowerYaw,
    string ThrowerPlace,
    Vector3 ProjectileStart,
    int? DetonateTick,
    Vector3? DetonatePosition,
    IReadOnlyList<FlashVictim> FlashVictims)
{
    public bool Detonated => DetonatePosition.HasValue;

    /// <summary>Как стоял бросивший (присед, в прыжке, шагом); null — демка разобрана версией без этих данных.</summary>
    public MovementFlags? ThrowerMovement { get; init; }

    /// <summary>Горизонтальная скорость бросившего в момент броска, units/s; null — нет данных.</summary>
    public float? ThrowerSpeed { get; init; }
}
