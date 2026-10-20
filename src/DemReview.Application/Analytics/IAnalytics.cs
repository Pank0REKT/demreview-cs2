namespace DemReview.Application.Analytics;

/// <summary>
/// Анонимная статистика использования. Отправка — в фоне и только если она включена в настройках и ключ
/// задан в сборке; ошибки сети не влияют на работу программы.
/// </summary>
public interface IAnalytics
{
    /// <summary>Значения свойств — строки или числа.</summary>
    void Track(string eventName, IReadOnlyDictionary<string, object>? props = null);
}

/// <summary>События статистики.</summary>
public static class AnalyticsEvents
{
    /// <summary>Программа запущена.</summary>
    public const string AppStarted = "app_started";

    /// <summary>Демка загружена (разобрана или взята из кеша).</summary>
    public const string DemoLoaded = "demo_loaded";
}
