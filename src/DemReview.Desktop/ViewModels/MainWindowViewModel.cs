using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemReview.Application;
using DemReview.Application.Parsing;
using DemReview.Desktop.Services;
using DemReview.Domain;
using Microsoft.Extensions.Logging;

namespace DemReview.Desktop.ViewModels;

public partial class MainWindowViewModel(
    IMatchLoader loader,
    IFileDialogService fileDialog,
    IPlatformService platform,
    IUiDispatcher dispatcher,
    StartupOptions startup,
    RadarViewModel radar,
    StatisticsViewModel statistics,
    ProfileViewModel profile,
    ILogger<MainWindowViewModel> logger) : ViewModelBase
{
    private CancellationTokenSource? _loadCts;

    [ObservableProperty]
    public partial MatchData? Match { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial double ProgressPercent { get; private set; }

    [ObservableProperty]
    public partial string StatusText { get; private set; } = "Откройте демку CS2: Ctrl+O, перетаскивание файла на окно или «Открыть с помощью».";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedPlayer))]
    public partial PlayerRowViewModel? SelectedPlayer { get; set; }

    [ObservableProperty]
    public partial string? CurrentPath { get; private set; }

    public RadarViewModel Radar { get; } = radar;

    public StatisticsViewModel Statistics { get; } = statistics;

    public FilterViewModel Filter { get; } = CreateFilter(radar, statistics, logger);

    public ProfileViewModel Profile { get; } = profile;

    /// <summary>Вкладка центральной области: 0 — радар, 1 — статистика, 2 — профиль.</summary>
    [ObservableProperty]
    public partial int SelectedTabIndex { get; set; }

    /// <summary>Фильтры применяются к радару и статистике.</summary>
    private static FilterViewModel CreateFilter(RadarViewModel radar, StatisticsViewModel statistics, ILogger logger)
    {
        var filter = new FilterViewModel();
        filter.Changed += async f =>
        {
            try
            {
                statistics.SetFilter(f);
                await radar.SetFilterAsync(f);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ошибка применения фильтра");
            }
        };
        return filter;
    }

    public ObservableCollection<PlayerRowViewModel> Players { get; } = [];

    public bool IsIdle => !IsBusy;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public bool HasMatch => Match is not null;

    public bool HasSelectedPlayer => SelectedPlayer is not null;

    public string WindowTitle => CurrentPath is null ? "DemReview" : $"{Path.GetFileName(CurrentPath)} — DemReview";

    public string MatchSummary => Match is not { } m
        ? ""
        : $"{m.MapName} · {m.FinalScore.TeamTName} {m.FinalScore.TeamTScore} : {m.FinalScore.TeamCTScore} {m.FinalScore.TeamCTName} · раундов: {m.Rounds.Count}";

    partial void OnMatchChanged(MatchData? value)
    {
        OnPropertyChanged(nameof(HasMatch));
        OnPropertyChanged(nameof(MatchSummary));
    }

    partial void OnCurrentPathChanged(string? value) => OnPropertyChanged(nameof(WindowTitle));

    partial void OnSelectedPlayerChanged(PlayerRowViewModel? value) => _ = ShowPlayerAsync(value?.SteamId);

    private async Task ShowPlayerAsync(ulong? steamId)
    {
        try
        {
            await Task.WhenAll(Radar.ShowPlayerAsync(steamId), Statistics.ShowPlayerAsync(steamId), Profile.ShowPlayerAsync(steamId));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ошибка анализа игрока {SteamId}", steamId);
            ErrorMessage = $"Ошибка анализа игрока: {ex.Message}";
        }
    }

    /// <summary>Вызывается после показа окна: открывает файл из командной строки.</summary>
    public Task InitializeAsync()
    {
        Profile.ShowOnRadarRequested += (pattern, switchTab) =>
        {
            Radar.FocusPattern(pattern);
            if (switchTab)
                SelectedTabIndex = 0;
        };
        return startup.DemoPath is { } path ? LoadFileAsync(path) : Task.CompletedTask;
    }

    [RelayCommand]
    private async Task OpenAsync()
    {
        var path = await fileDialog.PickDemoFileAsync();
        if (path is not null)
            await LoadFileAsync(path);
    }

    [RelayCommand]
    private Task ReloadAsync() => CurrentPath is { } path ? LoadFileAsync(path) : Task.CompletedTask;

    /// <summary>Разобрать демку заново, минуя кеш (например, после изменения шага сэмплов).</summary>
    [RelayCommand]
    private Task ReparseAsync() => CurrentPath is { } path ? LoadAsync(path, bypassCache: true) : Task.CompletedTask;

    [RelayCommand]
    private Task ExportAsync() => Profile.ExportCommand.ExecuteAsync(null);

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel() => _loadCts?.Cancel();

    [RelayCommand]
    private void DismissError() => ErrorMessage = null;

    [RelayCommand]
    private void OpenMapsFolder() => platform.OpenFolder(platform.MapsDirectory);

    [RelayCommand]
    private void OpenDataFolder() => platform.OpenFolder(platform.AppDataDirectory);

    /// <summary>Загрузка демки: разбор в фоне, прогресс и отмена.</summary>
    [RelayCommand]
    public Task LoadFileAsync(string path) => LoadAsync(path, bypassCache: false);

    private async Task LoadAsync(string path, bool bypassCache)
    {
        _loadCts?.Cancel();
        using var cts = new CancellationTokenSource();
        _loadCts = cts;

        ErrorMessage = null;
        IsBusy = true;
        ProgressPercent = 0;
        StatusText = $"Открываю {Path.GetFileName(path)}…";

        var progress = new UiProgress<ParseProgress>(dispatcher, p =>
        {
            if (_loadCts != cts)
                return;
            ProgressPercent = p.Fraction * 100;
            StatusText = p.Stage;
        });

        try
        {
            var match = await loader.LoadAsync(path, progress, cts.Token, bypassCache);
            ShowMatch(match, path);
            StatusText = $"Загружено: {Path.GetFileName(path)}";
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            if (_loadCts == cts)
                StatusText = "Разбор отменён.";
        }
        catch (DemoParseException ex)
        {
            ErrorMessage = ex.Message;
            StatusText = "Не удалось открыть демку.";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Непредвиденная ошибка при загрузке {Path}", path);
            ErrorMessage = $"Непредвиденная ошибка: {ex.Message}. Подробности — в журнале.";
            StatusText = "Не удалось открыть демку.";
        }
        finally
        {
            if (_loadCts == cts)
            {
                _loadCts = null;
                IsBusy = false;
            }
        }
    }

    private void ShowMatch(MatchData match, string path)
    {
        var previousSteamId = SelectedPlayer?.SteamId;

        Match = match;
        CurrentPath = path;
        Radar.SetMatch(match);
        Statistics.SetMatch(match);
        Filter.SetMatch(match);
        Profile.SetMatch(match);

        Players.Clear();
        foreach (var player in match.Players)
            Players.Add(new PlayerRowViewModel(player));


        SelectedPlayer = Players.FirstOrDefault(p => p.SteamId == previousSteamId);
    }
}
