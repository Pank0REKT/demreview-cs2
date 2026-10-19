using System.Numerics;

namespace DemReview.Domain;

/// <summary>Флаги движения игрока в момент сэмпла.</summary>
[Flags]
public enum MovementFlags : byte
{
    None = 0,
    Ducking = 1,
    Airborne = 2,
    Walking = 4,
    Scoped = 8,
}

/// <summary>Состояние живого игрока в один тик (выборка с шагом N тиков).</summary>
/// <param name="TimeInRound">Секунды от конца фризтайма.</param>
/// <param name="Speed">Горизонтальная скорость, units/s.</param>
/// <param name="Weapon">Активное оружие без префикса weapon_ (ak47, usp_silencer, knife…).</param>
/// <param name="Place">Название зоны карты (LastPlaceName), может быть пустым.</param>
public readonly record struct PlayerSample(
    int Tick,
    int Round,
    float TimeInRound,
    Vector3 Position,
    float Pitch,
    float Yaw,
    float Speed,
    MovementFlags Flags,
    string Weapon,
    short Health,
    short Armor,
    int Money,
    int EquipmentValue,
    string Place);

/// <summary>Все сэмплы одного игрока, упорядоченные по тику.</summary>
public sealed record PlayerTrack(ulong SteamId, IReadOnlyList<PlayerSample> Samples)
{
    public IEnumerable<PlayerSample> InRound(int round) => Samples.Where(s => s.Round == round);
}
