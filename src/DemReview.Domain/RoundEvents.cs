using System.Numerics;

namespace DemReview.Domain;

public enum BombEventType
{
    Planted,
    Defused,
    Exploded,
}

public enum BombSite
{
    Unknown,
    A,
    B,
}

/// <summary>Событие бомбы в раунде.</summary>
/// <param name="PlayerSteamId">Кто заложил/обезвредил (для взрыва — null).</param>
public sealed record BombEvent(int Tick, int Round, BombEventType Type, BombSite Site, Vector3 Position, ulong? PlayerSteamId);

/// <summary>Урон игроку (player_hurt).</summary>
public sealed record DamageEvent(int Tick, int Round, ulong? AttackerSteamId, ulong VictimSteamId, int Damage, string Weapon);

/// <summary>Тип закупки игрока или команды в раунде.</summary>
public enum BuyType
{
    Pistol,
    Eco,
    Force,
    FullBuy,
}
