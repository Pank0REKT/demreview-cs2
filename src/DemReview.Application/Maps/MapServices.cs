using System.Numerics;
using DemReview.Domain;

namespace DemReview.Application.Maps;

/// <summary>Параметры радаров карт.</summary>
public interface IMapConfigProvider
{
    /// <summary>Конфиг карты или null, если карта неизвестна.</summary>
    MapConfig? Find(string mapName);

    IReadOnlyList<MapConfig> All { get; }
}

/// <summary>Поиск изображения радара в локальной папке maps/.</summary>
public interface IMapImageProvider
{
    /// <summary>Ожидаемый путь к PNG (даже если файла нет) — для подсказки пользователю.</summary>
    string ExpectedPath(string mapName);

    /// <summary>Путь к существующему PNG или null.</summary>
    string? FindImage(string mapName);
}

/// <summary>Перевод игровых координат в пиксели радара и обратно.</summary>
public interface IMapCoordinateConverter
{
    Vector2 ToRadar(MapConfig map, Vector3 world);

    Vector2 ToWorld(MapConfig map, Vector2 radar);

    bool IsLowerLevel(MapConfig map, Vector3 world);

    /// <summary>
    /// Конфиг для неизвестной карты: подбирается по границам позиций игроков так,
    /// чтобы все точки поместились в радар с отступом.
    /// </summary>
    MapConfig FitToPositions(string mapName, IEnumerable<Vector3> positions);
}

public sealed class MapCoordinateConverter : IMapCoordinateConverter
{
    public Vector2 ToRadar(MapConfig map, Vector3 world) =>
        new((world.X - map.PosX) / map.Scale, (map.PosY - world.Y) / map.Scale);

    public Vector2 ToWorld(MapConfig map, Vector2 radar) =>
        new(radar.X * map.Scale + map.PosX, map.PosY - radar.Y * map.Scale);

    public bool IsLowerLevel(MapConfig map, Vector3 world) =>
        map.LowerLevelMaxZ is { } maxZ && world.Z < maxZ;

    public MapConfig FitToPositions(string mapName, IEnumerable<Vector3> positions)
    {
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var p in positions)
        {
            minX = Math.Min(minX, p.X);
            minY = Math.Min(minY, p.Y);
            maxX = Math.Max(maxX, p.X);
            maxY = Math.Max(maxY, p.Y);
        }

        if (minX > maxX)
            return new MapConfig(mapName, mapName, -2048, 2048, 4);

        const float margin = 0.05f;
        var size = Math.Max(maxX - minX, maxY - minY);
        size = Math.Max(size, 512) * (1 + 2 * margin);
        var centerX = (minX + maxX) / 2;
        var centerY = (minY + maxY) / 2;
        var scale = size / MapConfig.RadarSize;
        return new MapConfig(mapName, mapName, centerX - size / 2, centerY + size / 2, scale);
    }
}
