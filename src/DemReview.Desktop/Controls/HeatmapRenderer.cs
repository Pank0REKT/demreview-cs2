using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using DemReview.Application.Heatmaps;

namespace DemReview.Desktop.Controls;

/// <summary>Рисует тепловую карту в <see cref="WriteableBitmap"/> (BGRA, премультиплицированная альфа).</summary>
public static class HeatmapRenderer
{
    /// <summary>Значения ниже порога не рисуются — подложка остаётся читаемой.</summary>
    private const float Threshold = 0.04f;

    public static WriteableBitmap Render(HeatmapData data)
    {
        var size = data.Size;
        var bitmap = new WriteableBitmap(new PixelSize(size, size), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        var pixels = new int[size * size];

        for (var i = 0; i < pixels.Length; i++)
        {
            var v = data.Values[i];
            if (v < Threshold)
                continue;

            var (r, g, b) = Ramp(v);
            // Квадратный корень — чтобы слабые области тоже были заметны.
            var a = Math.Clamp(MathF.Sqrt(v) * 0.85f, 0, 1);
            pixels[i] = unchecked((int)Pack(r * a, g * a, b * a, a));
        }

        using var buffer = bitmap.Lock();
        for (var y = 0; y < size; y++)
        {
            var row = buffer.Address + y * buffer.RowBytes;
            Marshal.Copy(pixels, y * size, row, size);
        }

        return bitmap;
    }

    /// <summary>Шкала: синий → голубой → зелёный → жёлтый → красный.</summary>
    private static (float R, float G, float B) Ramp(float v)
    {
        (float, float, float)[] stops =
        [
            (0.10f, 0.25f, 0.95f),
            (0.10f, 0.80f, 0.95f),
            (0.20f, 0.90f, 0.30f),
            (1.00f, 0.90f, 0.15f),
            (1.00f, 0.20f, 0.10f),
        ];
        var position = Math.Clamp(v, 0, 1) * (stops.Length - 1);
        var index = Math.Min((int)position, stops.Length - 2);
        var t = position - index;
        var (r1, g1, b1) = stops[index];
        var (r2, g2, b2) = stops[index + 1];
        return (r1 + (r2 - r1) * t, g1 + (g2 - g1) * t, b1 + (b2 - b1) * t);
    }

    private static uint Pack(float r, float g, float b, float a) =>
        (uint)(a * 255) << 24 | (uint)(r * 255) << 16 | (uint)(g * 255) << 8 | (uint)(b * 255);
}
