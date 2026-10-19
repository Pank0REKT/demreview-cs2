using Avalonia;
using Avalonia.Controls;
using DemReview.Application;

namespace DemReview.Desktop.Behaviors;

/// <summary>Восстанавливает и сохраняет размер, положение и состояние главного окна.</summary>
public static class WindowPlacementTracker
{
    public static void Attach(Window window, ISettingsService settings)
    {
        var saved = settings.Current.MainWindow;
        Restore(window, saved);

        // Запоминаем только «обычные» размеры и положение: у свёрнутого окна координаты -32000,
        // у развёрнутого — размер экрана.
        var lastNormal = saved is { IsMaximized: false } && IsRealPosition(saved.X, saved.Y) ? saved : null;
        void Remember()
        {
            if (window.WindowState == WindowState.Normal && IsRealPosition(window.Position.X, window.Position.Y))
                lastNormal = new WindowPlacement(window.Position.X, window.Position.Y, window.Width, window.Height, false);
        }

        window.Opened += (_, _) => Remember();
        window.PositionChanged += (_, _) => Remember();
        window.Resized += (_, _) => Remember();

        window.Closing += (_, _) =>
        {
            Remember();
            if (lastNormal is null)
                return;

            var placement = lastNormal with { IsMaximized = window.WindowState == WindowState.Maximized };
            settings.Update(s => s with { MainWindow = placement });
        };
    }

    /// <summary>Windows ставит свёрнутому окну координаты (-32000, -32000) — такие не сохраняем.</summary>
    private static bool IsRealPosition(int x, int y) => x > -30000 && y > -30000;

    private static void Restore(Window window, WindowPlacement? placement)
    {
        if (placement is null || placement.Width < 400 || placement.Height < 300)
            return;

        window.Width = placement.Width;
        window.Height = placement.Height;

        // Положение восстанавливаем только если левый верхний угол попадает на один из текущих мониторов
        // (монитор могли отключить или поменять масштаб).
        var topLeft = new PixelPoint(placement.X, placement.Y);
        if (window.Screens.All.Any(s => s.WorkingArea.Contains(topLeft)))
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Position = topLeft;
        }

        if (placement.IsMaximized)
            window.WindowState = WindowState.Maximized;
    }
}
