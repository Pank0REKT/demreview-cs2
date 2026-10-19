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

    /// <summary>Тип закупки: средняя экипировка команды (или игрока) ниже — эко, $.</summary>
    public int BuyEcoMaxEquipment { get; init; } = 1500;

    /// <summary>Тип закупки: средняя экипировка не ниже — фулл-бай, между порогами — форс, $.</summary>
    public int BuyFullMinEquipment { get; init; } = 3700;

    /// <summary>Первый контакт (урон, убийство) не позже — агрессивный раунд, секунд от конца фризтайма.</summary>
    public float AggressiveContactSeconds { get; init; } = 20;

    /// <summary>Первый контакт не раньше (или его нет) — пассивный раунд, секунд.</summary>
    public float PassiveContactSeconds { get; init; } = 50;

    public WindowPlacement? MainWindow { get; init; }
}

/// <summary>Сохранённые размер и положение окна (в логических единицах, независимых от DPI).</summary>
public sealed record WindowPlacement(int X, int Y, double Width, double Height, bool IsMaximized);

public interface ISettingsService
{
    AppSettings Current { get; }

    void Update(Func<AppSettings, AppSettings> change);
}
