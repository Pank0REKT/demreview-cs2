using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemReview.Application;
using DemReview.Application.Analytics;
using DemReview.Application.Export;
using DemReview.Application.Localization;
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
    SettingsViewModel settings,
    IAnalytics analytics,
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
    public partial string StatusText { get; private set; } = IdleText;

    private static string IdleText => Loc.T("Откройте демку CS2: Ctrl+O, перетаскивание файла на окно или «Открыть с помощью».",
        "Open a CS2 demo: Ctrl+O, drag the file onto the window, or use \"Open with\".");

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

    public SettingsViewModel Settings { get; } = settings;

    /// <summary>Вкладка центральной области: 0 — радар, 1 — статистика, 2 — профиль, 3 — настройки.</summary>
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

    /// <summary>Панель «Кто вы в этой демке?»: предлагается, пока основной игрок не выбран.</summary>
    [ObservableProperty]
    public partial bool IsMainPlayerPromptVisible { get; private set; }

    /// <summary>Игроки для панели выбора себя: без ботов, по командам.</summary>
    public ObservableCollection<PlayerRowViewModel> MainPlayerCandidates { get; } = [];

    /// <summary>Демка, в которой пользователь ответил «Меня здесь нет» — в ней панель больше не показывается.</summary>
    private string? _mainPlayerPromptSkippedPath;

    public bool IsIdle => !IsBusy;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public bool HasMatch => Match is not null;

    public bool HasSelectedPlayer => SelectedPlayer is not null;

    public string WindowTitle => CurrentPath is null ? "DemReview" : $"{Path.GetFileName(CurrentPath)} — DemReview";

    public string MatchSummary => Match is not { } m
        ? ""
        : $"{m.MapName} · {ProfileExporter.TeamName(m.FinalScore.TeamTName, Side.T)} {m.FinalScore.TeamTScore} : {m.FinalScore.TeamCTScore} " +
          $"{ProfileExporter.TeamName(m.FinalScore.TeamCTName, Side.CT)} · {Loc.T("раундов", "rounds")}: {m.Rounds.Count}";

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
            ErrorMessage = Loc.T($"Ошибка анализа игрока: {ex.Message}", $"Player analysis failed: {ex.Message}");
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
        Settings.MainPlayerChanged += OnMainPlayerChanged;
        Settings.MainPlayerPickRequested += ShowMainPlayerPrompt;
        _ = Settings.CheckOnStartupAsync();
        analytics.Track(AnalyticsEvents.AppStarted, new Dictionary<string, object> { ["language"] = Loc.Language.ToString() });
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
        StatusText = Loc.T($"Открываю {Path.GetFileName(path)}…", $"Opening {Path.GetFileName(path)}…");

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
            StatusText = LoadedText(path);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            if (_loadCts == cts)
                StatusText = Loc.T("Разбор отменён.", "Parsing cancelled.");
        }
        catch (DemoParseException ex)
        {
            ErrorMessage = ex.Message;
            StatusText = Loc.T("Не удалось открыть демку.", "Could not open the demo.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Непредвиденная ошибка при загрузке {Path}", path);
            ErrorMessage = Loc.T($"Непредвиденная ошибка: {ex.Message}. Подробности — в журнале.", $"Unexpected error: {ex.Message}. See the log for details.");
            StatusText = Loc.T("Не удалось открыть демку.", "Could not open the demo.");
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

    private static string LoadedText(string path) => Loc.T($"Загружено: {Path.GetFileName(path)}", $"Loaded: {Path.GetFileName(path)}");

    partial void OnSelectedTabIndexChanged(int value)
    {
        if (value == SettingsTabIndex)
            _ = Settings.RefreshAsync();
    }

    public const int SettingsTabIndex = 3;

    [RelayCommand]
    private void OpenSettings() => SelectedTabIndex = SettingsTabIndex;

    /// <summary>Язык сменился: пересчитать все тексты (анализ формулирует их на текущем языке).</summary>
    public void RefreshLanguage()
    {
        ErrorMessage = null;
        Settings.RefreshLanguage();
        if (IsBusy)
            return;

        if (Match is { } match && CurrentPath is { } path)
        {
            ShowMatch(match, path);
            StatusText = LoadedText(path);
        }
        else
        {
            StatusText = IdleText;
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

        var mainSteamId = Settings.MainPlayerSteamId;
        Players.Clear();
        foreach (var player in match.Players)
            Players.Add(new PlayerRowViewModel(player) { IsMe = player.SteamId == mainSteamId });

        var me = Players.FirstOrDefault(p => p.IsMe);
        if (me is not null)
            Settings.RememberMainPlayerName(me.Name);

        // Тот же игрок, что был выбран (повторный анализ, смена языка), иначе — сам пользователь.
        SelectedPlayer = Players.FirstOrDefault(p => p.SteamId == previousSteamId) ?? me;

        MainPlayerCandidates.Clear();
        foreach (var player in Players.Where(p => !p.Player.IsBot).OrderBy(p => p.Side).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            MainPlayerCandidates.Add(player);
        IsMainPlayerPromptVisible = Settings.ShouldPromptForMainPlayer && path != _mainPlayerPromptSkippedPath && MainPlayerCandidates.Count > 0;
    }

    /// <summary>«Это я» на панели выбора: запомнить и сразу показать себя.</summary>
    [RelayCommand]
    private void ChooseMainPlayer(PlayerRowViewModel? row)
    {
        if (row is null)
            return;
        Settings.SetMainPlayer(row.SteamId, row.Name);
        SelectedPlayer = row;
    }

    [RelayCommand]
    private void SkipMainPlayerPrompt()
    {
        _mainPlayerPromptSkippedPath = CurrentPath;
        IsMainPlayerPromptVisible = false;
    }

    [RelayCommand]
    private void DisableMainPlayerPrompt()
    {
        Settings.AskForMainPlayer = false;
        IsMainPlayerPromptVisible = false;
    }

    /// <summary>«Выбрать в открытой демке» в настройках.</summary>
    private void ShowMainPlayerPrompt()
    {
        if (MainPlayerCandidates.Count == 0)
        {
            Settings.MainPlayerStatus = Loc.T("Сначала откройте демку, в которой вы играете.", "Open a demo you played in first.");
            return;
        }
        IsMainPlayerPromptVisible = true;
    }

    private void OnMainPlayerChanged(ulong? steamId)
    {
        foreach (var row in Players)
            row.IsMe = row.SteamId == steamId;
        if (steamId is not null)
            IsMainPlayerPromptVisible = false;
        if (Players.FirstOrDefault(p => p.IsMe) is { } me)
            Settings.RememberMainPlayerName(me.Name);
    }
}
