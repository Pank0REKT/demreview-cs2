namespace DemReview.Desktop.Services;

/// <summary>Параметры запуска: путь к демке из командной строки («Открыть с помощью» в Windows).</summary>
public sealed record StartupOptions(string? DemoPath)
{
    public static string? FindDemoPath(IEnumerable<string> args) =>
        args.Select(a => a.Trim().Trim('"'))
            .FirstOrDefault(a => a.EndsWith(".dem", StringComparison.OrdinalIgnoreCase) && File.Exists(a));
}
