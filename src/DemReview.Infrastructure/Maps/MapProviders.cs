using System.Text.Json;
using System.Text.Json.Serialization;
using DemReview.Application;
using DemReview.Application.Maps;
using DemReview.Domain;

namespace DemReview.Infrastructure.Maps;

/// <summary>Конфиги радаров из встроенного ресурса maps.json.</summary>
public sealed class EmbeddedMapConfigProvider : IMapConfigProvider
{
    /// <summary>Карты, калибровка которых сверена с позициями игроков в реальных демках.</summary>
    private static readonly HashSet<string> VerifiedMaps = ["de_mirage", "de_anubis"];

    private readonly Dictionary<string, MapConfig> _maps;

    public EmbeddedMapConfigProvider()
    {
        using var stream = typeof(EmbeddedMapConfigProvider).Assembly
            .GetManifestResourceStream("DemReview.Infrastructure.Maps.maps.json")
            ?? throw new InvalidOperationException("Не найден встроенный ресурс maps.json");

        var file = JsonSerializer.Deserialize<MapsFile>(stream) ?? throw new InvalidOperationException("Пустой maps.json");
        _maps = file.Maps.ToDictionary(
            m => m.Name.ToLowerInvariant(),
            m => new MapConfig(m.Name.ToLowerInvariant(), m.DisplayName, m.PosX, m.PosY, m.Scale, m.LowerLevelMaxZ,
                VerifiedMaps.Contains(m.Name)),
            StringComparer.OrdinalIgnoreCase);
        All = _maps.Values.OrderBy(m => m.Name).ToList();
    }

    public IReadOnlyList<MapConfig> All { get; }

    public MapConfig? Find(string mapName) =>
        _maps.GetValueOrDefault(NormalizeMapName(mapName));

    /// <summary>Имя карты из демки может содержать путь воркшопа: workshop/123/de_mirage → de_mirage.</summary>
    internal static string NormalizeMapName(string mapName) =>
        mapName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.ToLowerInvariant() ?? "";

    private sealed record MapsFile([property: JsonPropertyName("maps")] List<MapEntry> Maps);

    private sealed record MapEntry(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("displayName")] string DisplayName,
        [property: JsonPropertyName("pos_x")] float PosX,
        [property: JsonPropertyName("pos_y")] float PosY,
        [property: JsonPropertyName("scale")] float Scale,
        [property: JsonPropertyName("lowerLevelMaxZ")] float? LowerLevelMaxZ);
}

/// <summary>Изображения радаров: maps/&lt;карта&gt;.png рядом с приложением (имена строго в нижнем регистре).</summary>
public sealed class LocalMapImageProvider(IPlatformService platform) : IMapImageProvider
{
    public string ExpectedPath(string mapName) =>
        Path.Combine(platform.MapsDirectory, EmbeddedMapConfigProvider.NormalizeMapName(mapName) + ".png");

    public string? FindImage(string mapName)
    {
        var path = ExpectedPath(mapName);
        return File.Exists(path) ? path : null;
    }
}
