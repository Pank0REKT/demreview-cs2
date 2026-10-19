using System.Numerics;
using DemReview.Domain;

namespace DemReview.Application.Maps;

/// <summary>
/// Определяет название зоны для произвольной точки карты по ближайшим позициям игроков матча
/// (у каждой позиции есть LastPlaceName). Нужен для точек, где игроков не было, — например, места детонации гранаты.
/// </summary>
public sealed class ZoneLocator
{
    private const float CellSize = 128;
    private readonly Dictionary<(int, int), List<(Vector3 Position, string Place)>> _grid = [];

    public ZoneLocator(MatchData match)
    {
        foreach (var sample in match.Tracks.Values.SelectMany(t => t.Samples))
        {
            if (sample.Place.Length == 0)
                continue;

            var key = Cell(sample.Position);
            if (!_grid.TryGetValue(key, out var list))
                _grid[key] = list = [];

            // В клетке достаточно нескольких десятков точек — дальше они не добавляют точности.
            if (list.Count < 64)
                list.Add((sample.Position, sample.Place));
        }
    }

    /// <summary>Зона ближайшей позиции игрока в радиусе <paramref name="maxDistance"/> или пустая строка.</summary>
    public string Locate(Vector3 point, float maxDistance = 400)
    {
        var (cx, cy) = Cell(point);
        var radius = (int)MathF.Ceiling(maxDistance / CellSize);
        var best = "";
        var bestDistance = maxDistance * maxDistance;

        for (var dx = -radius; dx <= radius; dx++)
        for (var dy = -radius; dy <= radius; dy++)
        {
            if (!_grid.TryGetValue((cx + dx, cy + dy), out var list))
                continue;

            foreach (var (position, place) in list)
            {
                // Высота важна меньше: граната может взорваться в воздухе над зоной.
                var d = Vector2.DistanceSquared(new Vector2(point.X, point.Y), new Vector2(position.X, position.Y))
                        + MathF.Pow((point.Z - position.Z) * 0.5f, 2);
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = place;
                }
            }
        }

        return best;
    }

    private static (int, int) Cell(Vector3 p) => ((int)MathF.Floor(p.X / CellSize), (int)MathF.Floor(p.Y / CellSize));
}
