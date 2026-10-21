using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemReview.Application;
using DemReview.Application.Localization;
using DemReview.Application.Parsing;
using DemReview.Application.Updates;
using DemReview.Desktop.Services;
using Microsoft.Extensions.Logging;

namespace DemReview.Desktop.ViewModels;

public sealed record LanguageOption(UiLanguage Language, string Name);

/// <summary>Вкладка «Настройки»: язык интерфейса, основной игрок, кеш разобранных демок, каталоги, обновления, статистика.</summary>
public partial class SettingsViewModel : ViewModelBase
{
    private readonly LanguageService _language;
    private readonly IMatchCache _cache;
    private readonly IPlatformService _platform;
    private readonly ILogger<SettingsViewModel> _logger;

    public SettingsViewModel(LanguageService language, IMatchCache cache, IPlatformService platform, IUpdateChecker updates, AppInfo app,
        ISettingsService settings, ILogger<SettingsViewModel> logger)
    {
        _language = language;
        _cache = cache;
        _platform = platform;
        _updates = updates;
        _app = app;
        _settings = settings;
        CheckForUpdates = settings.Current.CheckForUpdates;
        SendUsageStatistics = settings.Current.SendUsageStatistics;
        AskForMainPlayer = !settings.Current.MainPlayerPromptDisabled;
        MainPlayerInput = settings.Current.MainPlayerSteamId?.ToString() ?? "";
        _logger = logger;
        SelectedLanguage = Languages.First(l => l.Language == Loc.Language);
    }

    public IReadOnlyList<LanguageOption> Languages { get; } =
        Enum.GetValues<UiLanguage>().Select(l => new LanguageOption(l, Loc.NativeName(l))).ToList();

    [ObservableProperty]
    public partial LanguageOption SelectedLanguage { get; set; }

    [ObservableProperty]
    public partial string CacheText { get; private set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ClearCacheCommand))]
    public partial bool IsCacheBusy { get; private set; }

    [ObservableProperty]
    public partial string? CacheStatus { get; private set; }

    public string DataDirectory => _platform.AppDataDirectory;

    public string MapsDirectory => _platform.MapsDirectory;

    public string Version => _app.VersionText;

    /// <summary>Анонимная статистика использования; выключение действует сразу.</summary>
    [ObservableProperty]
    public partial bool SendUsageStatistics { get; set; }

    /// <summary>Ключ статистики задан в сборке (только релизы) — иначе отправлять нечего и флажок неактивен.</summary>
    public bool HasAnalytics => _app.HasAnalytics;

    partial void OnSendUsageStatisticsChanged(bool value) => _settings.Update(s => s with { SendUsageStatistics = value });

    partial void OnSelectedLanguageChanged(LanguageOption value)
    {
        if (value is not null && value.Language != Loc.Language)
            _language.Apply(value.Language, save: true);
    }

    /// <summary>Язык сменился: тексты сведений о кеше — заново.</summary>
    public void RefreshLanguage()
    {
        CacheStatus = null;
        MainPlayerStatus = null;
        OnPropertyChanged(nameof(MainPlayerText));
        OnPropertyChanged(nameof(MainPlayerName));
        OnPropertyChanged(nameof(MainPlayerInitials));
        _ = RefreshAsync();
    }

    /// <summary>Обновить сведения о кеше (при открытии вкладки).</summary>
    public async Task RefreshAsync()
    {
        var info = await Task.Run(_cache.GetInfo);
        CacheText = Describe(info);
        CacheEntries = info.Entries;
        CacheSizeText = SizeText(info.SizeBytes);
        // Пороги могли поменять на вкладке профиля — показать актуальные.
        _thresholds = null;
        OnPropertyChanged(nameof(Thresholds));
    }

    /// <summary>Сколько демок хранит кеш (старые вытесняются).</summary>
    public const int CacheLimit = 50;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CacheCountText))]
    public partial int CacheEntries { get; private set; }

    [ObservableProperty]
    public partial string CacheSizeText { get; private set; } = "";

    public string CacheLimitText => Loc.T($"из {CacheLimit} демок", $"of {CacheLimit} demos");

    public string CacheCountText => $"{CacheEntries}";

    public static double CacheMaximum => CacheLimit;

    public string MainPlayerName => _settings.Current.MainPlayerName is { Length: > 0 } name ? name
        : MainPlayerSteamId is not null ? Loc.T("Ник неизвестен", "Nickname unknown") : Loc.T("Не выбран", "Not selected");

    public string MainPlayerInitials => _settings.Current.MainPlayerName is { Length: > 0 } name ? Texts.Initials(name) : "?";

    private bool CanClearCache => !IsCacheBusy;

    [RelayCommand(CanExecute = nameof(CanClearCache))]
    private async Task ClearCacheAsync()
    {
        IsCacheBusy = true;
        CacheStatus = null;
        try
        {
            await Task.Run(_cache.Clear);
            CacheStatus = Loc.T("Кеш очищен. Демки будут разобраны заново при следующем открытии.",
                "Cache cleared. Demos will be parsed again the next time they are opened.");
            _logger.LogInformation("Кеш разобранных демок очищен пользователем");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось очистить кеш");
            CacheStatus = Loc.T($"Не удалось очистить кеш: {ex.Message}", $"Could not clear the cache: {ex.Message}");
        }
        finally
        {
            IsCacheBusy = false;
        }
        await RefreshAsync();
    }

    [RelayCommand]
    private void OpenDataFolder() => _platform.OpenFolder(_platform.AppDataDirectory);

    [RelayCommand]
    private void OpenMapsFolder() => _platform.OpenFolder(_platform.MapsDirectory);

    internal static string SizeText(long bytes) => bytes >= 1024 * 1024
        ? Loc.T($"{bytes / 1024.0 / 1024:F1} МБ", $"{bytes / 1024.0 / 1024:F1} MB")
        : Loc.T($"{bytes / 1024.0:F0} КБ", $"{bytes / 1024.0:F0} KB");

    private static string Describe(MatchCacheInfo info)
    {
        var size = SizeText(info.SizeBytes);
        return info.Entries == 0
            ? Loc.T("Кеш пуст.", "The cache is empty.")
            : Loc.T($"Сохранено демок: {info.Entries}, на диске: {size}.", $"Demos saved: {info.Entries}, size on disk: {size}.");
    }
}
