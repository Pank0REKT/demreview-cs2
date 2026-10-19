namespace DemReview.Application;

/// <summary>Пользовательские настройки приложения.</summary>
public sealed record AppSettings
{
    /// <summary>Шаг выборки позиций, тиков.</summary>
    public int SampleTickStep { get; init; } = 16;

    /// <summary>Порог расстояния между точками броска в одной раскидке, units.</summary>
    public float GrenadeClusterThrowDistance { get; init; } = 150;

    /// <summary>Порог расстояния между точками детонации в одной раскидке, units.</summary>
    public float GrenadeClusterLandDistance { get; init; } = 250;

    public WindowPlacement? MainWindow { get; init; }
}

/// <summary>Сохранённые размер и положение окна (в логических единицах, независимых от DPI).</summary>
public sealed record WindowPlacement(int X, int Y, double Width, double Height, bool IsMaximized);

public interface ISettingsService
{
    AppSettings Current { get; }

    void Update(Func<AppSettings, AppSettings> change);
}
