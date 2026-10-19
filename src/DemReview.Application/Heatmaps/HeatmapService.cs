using System.Numerics;

namespace DemReview.Application.Heatmaps;

/// <summary>Плотность точек на радаре: сетка Size×Size, значения 0..1 (нормированы на максимум).</summary>
public sealed record HeatmapData(int Size, float[] Values, int PointCount)
{
    public float this[int x, int y] => Values[y * Size + x];
}

/// <param name="Size">Размер сетки (радар 1024 px делится на Size ячеек).</param>
/// <param name="BlurRadius">Радиус гауссова размытия в ячейках.</param>
public sealed record HeatmapOptions(int Size = 256, int BlurRadius = 4);

public interface IHeatmapService
{
    /// <summary>Строит тепловую карту по точкам в пикселях радара (0..1024).</summary>
    HeatmapData Build(IEnumerable<Vector2> radarPoints, HeatmapOptions? options = null);
}

public sealed class HeatmapService : IHeatmapService
{
    private const float RadarSize = 1024;

    public HeatmapData Build(IEnumerable<Vector2> radarPoints, HeatmapOptions? options = null)
    {
        options ??= new HeatmapOptions();
        var size = options.Size;
        var grid = new float[size * size];
        var count = 0;

        foreach (var p in radarPoints)
        {
            var x = (int)MathF.Floor(p.X / RadarSize * size);
            var y = (int)MathF.Floor(p.Y / RadarSize * size);
            if (x < 0 || y < 0 || x >= size || y >= size)
                continue;
            grid[y * size + x] += 1;
            count++;
        }

        if (count == 0)
            return new HeatmapData(size, grid, 0);

        var blurred = Blur(grid, size, options.BlurRadius);
        var max = blurred.Max();
        if (max > 0)
        {
            for (var i = 0; i < blurred.Length; i++)
                blurred[i] /= max;
        }

        return new HeatmapData(size, blurred, count);
    }

    /// <summary>Разделимое гауссово размытие (по строкам, затем по столбцам).</summary>
    internal static float[] Blur(float[] source, int size, int radius)
    {
        if (radius <= 0)
            return (float[])source.Clone();

        var sigma = radius / 2f;
        var kernel = new float[radius * 2 + 1];
        for (var i = -radius; i <= radius; i++)
            kernel[i + radius] = MathF.Exp(-(i * i) / (2 * sigma * sigma));
        var sum = kernel.Sum();
        for (var i = 0; i < kernel.Length; i++)
            kernel[i] /= sum;

        var temp = new float[source.Length];
        var result = new float[source.Length];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var acc = 0f;
            for (var k = -radius; k <= radius; k++)
            {
                var xx = x + k;
                if (xx >= 0 && xx < size)
                    acc += source[y * size + xx] * kernel[k + radius];
            }
            temp[y * size + x] = acc;
        }

        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var acc = 0f;
            for (var k = -radius; k <= radius; k++)
            {
                var yy = y + k;
                if (yy >= 0 && yy < size)
                    acc += temp[yy * size + x] * kernel[k + radius];
            }
            result[y * size + x] = acc;
        }

        return result;
    }
}
