namespace DemReview.Application;

/// <summary>Пользовательские настройки приложения.</summary>
public sealed record AppSettings
{
    /// <summary>Язык интерфейса: «ru» или «en»; null — ещё не выбран (спросить при запуске).</summary>
    public string? Language { get; init; }

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

    /// <summary>Паттерн показывается при частоте не ниже (0..1).</summary>
    public double PatternMinShare { get; init; } = 0.6;

    /// <summary>Паттерн показывается при числе примеров не меньше.</summary>
    public int PatternMinExamples { get; init; } = 3;

    /// <summary>Проверять новые релизы на GitHub при запуске (один запрос, без него всё работает офлайн).</summary>
    public bool CheckForUpdates { get; init; } = true;

    /// <summary>Версия, о которой пользователь попросил больше не напоминать.</summary>
    public string? DismissedUpdateVersion { get; init; }

    /// <summary>Отправлять анонимную статистику: запуск программы и загрузка демки (карта, без имён и файлов).</summary>
    public bool SendUsageStatistics { get; init; } = true;

    /// <summary>Основной игрок (сам пользователь): выбирается автоматически и отмечается в списке игроков. Хранится только локально.</summary>
    public ulong? MainPlayerSteamId { get; init; }

    /// <summary>Последний известный ник основного игрока — для подписи в настройках.</summary>
    public string? MainPlayerName { get; init; }

    /// <summary>Пользователь попросил не предлагать выбрать себя при открытии демки.</summary>
    public bool MainPlayerPromptDisabled { get; init; }

    /// <summary>Папки с демками, добавленные в библиотеку: при обновлении из них разбираются новые демки.</summary>
    public IReadOnlyList<string> DemoFolders { get; init; } = [];

    /// <summary>Сохранять в библиотеку каждую открытую демку, где играет основной игрок, — из них собирается «Мой профиль».</summary>
    public bool SaveMyDemosToLibrary { get; init; } = true;

    public WindowPlacement? MainWindow { get; init; }
}

/// <summary>Сохранённые размер и положение окна (в логических единицах, независимых от DPI).</summary>
public sealed record WindowPlacement(int X, int Y, double Width, double Height, bool IsMaximized);

public interface ISettingsService
{
    AppSettings Current { get; }

    void Update(Func<AppSettings, AppSettings> change);
}
