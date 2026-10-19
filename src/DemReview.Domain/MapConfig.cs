namespace DemReview.Domain;

/// <summary>
/// Параметры радара карты из overview-файлов CS2: игровые координаты → пиксели изображения 1024×1024.
/// px = (x − PosX) / Scale, py = (PosY − y) / Scale.
/// </summary>
/// <param name="LowerLevelMaxZ">Для многоуровневых карт (nuke): точки ниже этой высоты — нижний уровень.</param>
/// <param name="IsVerified">Калибровка сверена с позициями игроков из реальных демок.</param>
public sealed record MapConfig(
    string Name,
    string DisplayName,
    float PosX,
    float PosY,
    float Scale,
    float? LowerLevelMaxZ = null,
    bool IsVerified = false)
{
    public const int RadarSize = 1024;
}
