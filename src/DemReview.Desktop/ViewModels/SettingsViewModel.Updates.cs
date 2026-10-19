using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemReview.Application;
using DemReview.Application.Localization;
using DemReview.Application.Updates;
using Microsoft.Extensions.Logging;

namespace DemReview.Desktop.ViewModels;

/// <summary>Проверка новых релизов: при запуске (если включено) и вручную; баннер в главном окне.</summary>
public partial class SettingsViewModel
{
    private readonly IUpdateChecker _updates;
    private readonly AppInfo _app;
    private readonly ISettingsService _settings;
    private UpdateCheckResult? _lastCheck;

    [ObservableProperty]
    public partial bool CheckForUpdates { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckUpdatesNowCommand))]
    public partial bool IsCheckingUpdates { get; private set; }

    /// <summary>Новая версия, о которой показан баннер.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdateBanner), nameof(UpdateBannerText))]
    public partial ReleaseInfo? BannerRelease { get; private set; }

    public bool HasUpdateBanner => BannerRelease is not null;

    public string UpdateBannerText => BannerRelease is not { } r ? ""
        : Loc.T($"Доступна новая версия DemReview {r.Version.ToString(3)} (у вас {_app.VersionText}).",
            $"DemReview {r.Version.ToString(3)} is available (you have {_app.VersionText}).");

    /// <summary>Адрес репозитория задан — проверка и ссылка на страницу проекта доступны.</summary>
    public bool HasRepository => _app.HasRepository;

    public string UpdateStatusText => _lastCheck?.Status switch
    {
        null => "",
        UpdateStatus.UpToDate => Loc.T($"Установлена последняя версия ({_app.VersionText}).", $"You have the latest version ({_app.VersionText})."),
        UpdateStatus.UpdateAvailable => Loc.T($"Доступна версия {_lastCheck.Latest!.Version.ToString(3)}.", $"Version {_lastCheck.Latest!.Version.ToString(3)} is available."),
        UpdateStatus.NotConfigured => Loc.T("Адрес репозитория не задан в сборке — проверка недоступна.", "The repository address is not set in this build — checking is unavailable."),
        _ => Loc.T("Не удалось проверить обновления (нет сети или GitHub недоступен).", "Could not check for updates (no network or GitHub is unavailable)."),
    };

    partial void OnCheckForUpdatesChanged(bool value) => _settings.Update(s => s with { CheckForUpdates = value });

    /// <summary>Проверка при запуске: тихо, без сообщений об ошибках; баннер — только для непропущенной версии.</summary>
    public async Task CheckOnStartupAsync()
    {
        if (!CheckForUpdates || !_app.HasRepository)
            return;

        var result = await CheckAsync();
        if (result.Status == UpdateStatus.UpdateAvailable && result.Latest!.Tag != _settings.Current.DismissedUpdateVersion)
            BannerRelease = result.Latest;
    }

    private bool CanCheckUpdates => !IsCheckingUpdates;

    [RelayCommand(CanExecute = nameof(CanCheckUpdates))]
    private async Task CheckUpdatesNowAsync()
    {
        var result = await CheckAsync();
        if (result.Status == UpdateStatus.UpdateAvailable)
            BannerRelease = result.Latest;
    }

    private async Task<UpdateCheckResult> CheckAsync()
    {
        IsCheckingUpdates = true;
        try
        {
            _lastCheck = await _updates.CheckAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ошибка проверки обновлений");
            _lastCheck = new UpdateCheckResult(UpdateStatus.Failed, Error: ex.Message);
        }
        finally
        {
            IsCheckingUpdates = false;
        }
        OnPropertyChanged(nameof(UpdateStatusText));
        return _lastCheck;
    }

    [RelayCommand]
    private void OpenRelease()
    {
        var url = BannerRelease?.Url ?? _lastCheck?.Latest?.Url;
        if (url is not null)
            OpenUrl(url);
    }

    /// <summary>Больше не напоминать об этой версии (о следующей — напомнить).</summary>
    [RelayCommand]
    private void DismissUpdate()
    {
        if (BannerRelease is { } release)
            _settings.Update(s => s with { DismissedUpdateVersion = release.Tag });
        BannerRelease = null;
    }

    [RelayCommand]
    private void OpenProjectPage()
    {
        if (_app.HasRepository)
            OpenUrl(_app.RepositoryUrl);
    }

    private void OpenUrl(string url)
    {
        try
        {
            _platform.OpenUrl(url);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось открыть ссылку {Url}", url);
        }
    }
}
