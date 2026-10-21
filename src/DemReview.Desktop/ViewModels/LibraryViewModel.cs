using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemReview.Application;
using DemReview.Application.Library;
using DemReview.Application.Localization;
using DemReview.Application.Parsing;
using DemReview.Desktop.Services;
using DemReview.Domain;
using Microsoft.Extensions.Logging;

namespace DemReview.Desktop.ViewModels;

/// <summary>
/// Раздел «Библиотека»: папки с демками, сохранённые анализы и сводный профиль игрока по нескольким демкам
/// одной карты. Профиль строится из библиотеки — без исходных файлов и без повторного разбора.
/// </summary>
public partial class LibraryViewModel(
    IDemoLibrary library,
    IDemoLibraryService service,
    ISettingsService settings,
    IFileDialogService fileDialog,
    IPlatformService platform,
    IUiDispatcher dispatcher,
    ILogger<LibraryViewModel> logger) : ViewModelBase
{
    /// <summary>Рекомендуемый минимум демок: по одной демке выборка мала.</summary>
    public const int RecommendedDemos = 3;

    private IReadOnlyList<LibraryEntry> _entries = [];
    private CancellationTokenSource? _cts;
    private bool _filling;

    /// <summary>Сводный матч собран: показать профиль игрока.</summary>
    public event Action<MatchData, ulong>? CombinedReady;

    /// <summary>Открыть одну демку из библиотеки (из файла или, если файла уже нет, из сохранённого анализа).</summary>
    public event Action<LibraryEntry>? OpenDemoRequested;

    public ObservableCollection<LibraryFolderViewModel> Folders { get; } = [];

    public ObservableCollection<LibraryPlayerOption> Players { get; } = [];

    public ObservableCollection<LibraryMapOption> Maps { get; } = [];

    public ObservableCollection<LibraryDemoRowViewModel> Demos { get; } = [];

    [ObservableProperty]
    public partial LibraryPlayerOption? SelectedPlayer { get; set; }

    [ObservableProperty]
    public partial LibraryMapOption? SelectedMap { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(BuildProfileCommand), nameof(AddFolderCommand), nameof(RescanCommand), nameof(CancelCommand))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial double ProgressPercent { get; private set; }

    [ObservableProperty]
    public partial string? ProgressText { get; private set; }

    [ObservableProperty]
    public partial string? StatusText { get; private set; }

    [ObservableProperty]
    public partial bool SaveMyDemos { get; set; } = settings.Current.SaveMyDemosToLibrary;

    partial void OnSaveMyDemosChanged(bool value) => settings.Update(s => s with { SaveMyDemosToLibrary = value });

    public bool IsIdle => !IsBusy;

    public bool HasEntries => _entries.Count > 0;

    public bool HasFolders => Folders.Count > 0;

    public bool HasMainPlayer => settings.Current.MainPlayerSteamId is not null;

    /// <summary>«В библиотеке 34 демки · 87 МБ».</summary>
    [ObservableProperty]
    public partial string Summary { get; private set; } = "";

    /// <summary>«Выбрано 5 демок · 112 раундов».</summary>
    public string SelectionText
    {
        get
        {
            var selected = Demos.Where(d => d.IsIncluded).ToList();
            return Loc.T($"Выбрано демок: {selected.Count} · раундов: {selected.Sum(d => d.Entry.RoundCount)}",
                $"Selected demos: {selected.Count} · rounds: {selected.Sum(d => d.Entry.RoundCount)}");
        }
    }

    /// <summary>Подсказка о размере выборки.</summary>
    public string SelectionHint => Demos.Count(d => d.IsIncluded) switch
    {
        0 => Loc.T("Отметьте демки, по которым строить профиль.", "Tick the demos to build the profile from."),
        < 2 => Loc.T("Для сводного профиля нужно хотя бы две демки; одну демку можно просто открыть.",
            "A combined profile needs at least two demos; a single demo can simply be opened."),
        < RecommendedDemos => Loc.T($"Выводы уверенней с {RecommendedDemos} демок и больше.", $"Conclusions get more confident from {RecommendedDemos} demos up."),
        _ => Loc.T("Чем больше раундов, тем выше уверенность в каждом паттерне.", "More rounds mean more confidence in every pattern."),
    };

    private bool CanBuild => IsIdle && Demos.Count(d => d.IsIncluded) >= 2;

    /// <summary>Вызывается при запуске: загрузить список демок библиотеки.</summary>
    public Task InitializeAsync() => RefreshAsync();

    /// <summary>Перечитать библиотеку (после импорта, удаления, сохранения своей демки).</summary>
    public async Task RefreshAsync()
    {
        var (entries, info) = await Task.Run(() => (library.List(), library.GetInfo()));
        _entries = entries;
        Summary = Loc.T($"В библиотеке демок: {info.Entries} · {SettingsViewModel.SizeText(info.SizeBytes)}",
            $"Demos in the library: {info.Entries} · {SettingsViewModel.SizeText(info.SizeBytes)}");
        FillFolders();
        FillPlayers();
        OnPropertyChanged(nameof(HasEntries));
        OnPropertyChanged(nameof(HasMainPlayer));
    }

    /// <summary>Язык сменился — подписи заново.</summary>
    public void RefreshLanguage()
    {
        StatusText = null;
        _ = RefreshAsync();
    }

    /// <summary>
    /// «Мой профиль»: открытая демка, где играет основной игрок, сохраняется в библиотеку и добавляется к прошлым.
    /// </summary>
    public async Task RememberMyDemoAsync(MatchData match, string path)
    {
        if (!SaveMyDemos || match.IsCombined || settings.Current.MainPlayerSteamId is not { } me || match.FindPlayer(me) is null)
            return;

        try
        {
            var stored = await Task.Run(() =>
            {
                var key = DemoFileKey.FromFile(path, match.SampleTickStep);
                if (library.Contains(key))
                    return false;
                library.Store(key, match);
                return true;
            });
            if (stored)
                await RefreshAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Не удалось сохранить демку {Path} в библиотеку", path);
        }
    }

    /// <summary>Основной игрок сменился — он первым в списке и выбран.</summary>
    public void RefreshMainPlayer()
    {
        OnPropertyChanged(nameof(HasMainPlayer));
        FillPlayers();
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task AddFolderAsync()
    {
        if (await fileDialog.PickDemoFolderAsync() is not { } folder)
            return;

        if (!settings.Current.DemoFolders.Contains(folder, platform.PathComparer))
            settings.Update(s => s with { DemoFolders = [.. s.DemoFolders, folder] });
        await ImportAsync([folder]);
    }

    /// <summary>Обновить: разобрать новые и изменённые демки во всех папках.</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task RescanAsync() => ImportAsync(settings.Current.DemoFolders);

    [RelayCommand]
    private void RemoveFolder(LibraryFolderViewModel? folder)
    {
        if (folder is null)
            return;
        // Уже разобранные демки остаются в библиотеке: папка лишь перестаёт обновляться.
        settings.Update(s => s with { DemoFolders = s.DemoFolders.Where(f => !platform.PathComparer.Equals(f, folder.Path)).ToList() });
        FillFolders();
    }

    [RelayCommand]
    private void OpenFolder(LibraryFolderViewModel? folder)
    {
        if (folder is not null && Directory.Exists(folder.Path))
            platform.OpenFolder(folder.Path);
    }

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void ShowMyProfile()
    {
        if (Players.FirstOrDefault(p => p.IsMe) is { } me)
            SelectedPlayer = me;
    }

    [RelayCommand]
    private void SelectAll() => SetIncluded(true);

    [RelayCommand]
    private void SelectNone() => SetIncluded(false);

    [RelayCommand]
    private void OpenDemo(LibraryDemoRowViewModel? row)
    {
        if (row is not null)
            OpenDemoRequested?.Invoke(row.Entry);
    }

    [RelayCommand]
    private async Task RemoveDemoAsync(LibraryDemoRowViewModel? row)
    {
        if (row is null)
            return;
        await Task.Run(() => library.Remove(row.Entry.Id));
        StatusText = Loc.T($"Убрано из библиотеки: {row.FileName}. Файл демки не тронут.", $"Removed from the library: {row.FileName}. The demo file is untouched.");
        await RefreshAsync();
    }

    /// <summary>Первое нажатие «Очистить библиотеку» только просит подтвердить.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ClearButtonText))]
    public partial bool IsClearConfirming { get; private set; }

    public string ClearButtonText => IsClearConfirming
        ? Loc.T("Точно очистить? Нажмите ещё раз", "Really clear? Click again")
        : Loc.T("Очистить библиотеку", "Clear the library");

    [RelayCommand]
    private async Task ClearLibraryAsync()
    {
        if (!IsClearConfirming)
        {
            IsClearConfirming = true;
            return;
        }

        IsClearConfirming = false;
        await Task.Run(library.Clear);
        StatusText = Loc.T("Библиотека очищена. Файлы демок не тронуты.", "The library is cleared. Demo files are untouched.");
        await RefreshAsync();
    }

    /// <summary>Склеить отмеченные демки и показать сводный профиль.</summary>
    [RelayCommand(CanExecute = nameof(CanBuild))]
    private async Task BuildProfileAsync()
    {
        if (SelectedPlayer is not { } player)
            return;
        var entries = Demos.Where(d => d.IsIncluded).Select(d => d.Entry).ToList();

        using var cts = Begin();
        ProgressText = Loc.T($"Собираю профиль по {entries.Count} демкам…", $"Building the profile from {entries.Count} demos…");
        try
        {
            var match = await service.CombineAsync(entries, cts.Token);
            logger.LogInformation("Сводный профиль {SteamId}: {Map}, демок {Demos}, раундов {Rounds}", player.SteamId, match.MapName,
                entries.Count, match.Rounds.Count);
            StatusText = null;
            CombinedReady?.Invoke(match, player.SteamId);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            StatusText = Loc.T("Отменено.", "Cancelled.");
        }
        catch (DemoParseException ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            End(cts);
        }
    }

    private async Task ImportAsync(IReadOnlyList<string> folders)
    {
        if (folders.Count == 0)
        {
            StatusText = Loc.T("Сначала добавьте папку с демками.", "Add a demo folder first.");
            return;
        }

        using var cts = Begin();
        var (found, added, failed) = (0, 0, new List<(string Path, string Error)>());
        try
        {
            foreach (var folder in folders)
            {
                var progress = new UiProgress<ParseProgress>(dispatcher, p =>
                {
                    if (_cts != cts)
                        return;
                    ProgressPercent = p.Fraction * 100;
                    ProgressText = p.Stage;
                });
                try
                {
                    var result = await service.ImportFolderAsync(folder, progress, cts.Token);
                    (found, added) = (found + result.Found, added + result.Added);
                    failed.AddRange(result.Failed);
                }
                catch (DemoParseException ex)
                {
                    failed.Add((folder, ex.Message));
                }
                await RefreshAsync();
            }

            StatusText = ImportText(found, added, failed);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            StatusText = Loc.T("Импорт остановлен. Уже разобранные демки сохранены.", "Import stopped. Demos parsed so far are saved.");
            await RefreshAsync();
        }
        finally
        {
            End(cts);
        }
    }

    private static string ImportText(int found, int added, List<(string Path, string Error)> failed)
    {
        var text = Loc.T($"Найдено демок: {found}, добавлено: {added}.", $"Demos found: {found}, added: {added}.");
        if (failed.Count > 0)
        {
            var list = string.Join("; ", failed.Take(3).Select(f => $"{Path.GetFileName(f.Path)} — {f.Error}"));
            text += Loc.T($" Не удалось разобрать: {failed.Count} ({list}).", $" Could not parse: {failed.Count} ({list}).");
        }
        return text;
    }

    private CancellationTokenSource Begin()
    {
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        StatusText = null;
        ProgressPercent = 0;
        IsBusy = true;
        return cts;
    }

    private void End(CancellationTokenSource cts)
    {
        if (_cts != cts)
            return;
        _cts = null;
        IsBusy = false;
        ProgressText = null;
    }

    private void FillFolders()
    {
        Folders.Clear();
        foreach (var folder in settings.Current.DemoFolders)
        {
            var prefix = Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar;
            var count = _entries.Count(e => e.FullPath.StartsWith(prefix, platform.PathComparer == StringComparer.Ordinal
                ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase));
            Folders.Add(new LibraryFolderViewModel(folder, count, Directory.Exists(folder)));
        }
        OnPropertyChanged(nameof(HasFolders));
    }

    /// <summary>
    /// Игроки библиотеки: основной игрок первым, дальше — встречающиеся хотя бы в двух демках
    /// (если таких нет — все), по числу демок.
    /// </summary>
    private void FillPlayers()
    {
        var previous = SelectedPlayer?.SteamId;
        var me = settings.Current.MainPlayerSteamId;
        var all = _entries
            .Where(e => e.IsCurrent)
            .SelectMany(e => e.Players.Where(p => !p.IsBot).Select(p => (Entry: e, Player: p)))
            .GroupBy(x => x.Player.SteamId)
            .Select(g => new LibraryPlayerOption(g.Key, g.MaxBy(x => x.Entry.FileTimeUtc).Player.Name, g.Count(), g.Key == me))
            .ToList();
        var shown = all.Where(p => p.DemoCount >= 2 || p.IsMe).ToList();
        if (shown.Count == 0)
            shown = all;

        _filling = true;
        Players.Clear();
        foreach (var player in shown.OrderByDescending(p => p.IsMe).ThenByDescending(p => p.DemoCount).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            Players.Add(player);
        _filling = false;

        SelectedPlayer = Players.FirstOrDefault(p => p.SteamId == previous) ?? Players.FirstOrDefault();
        // Тот же игрок — выбор не меняется, но демки могли добавиться.
        if (SelectedPlayer?.SteamId == previous)
            FillMaps();
    }

    partial void OnSelectedPlayerChanged(LibraryPlayerOption? value)
    {
        if (!_filling)
            FillMaps();
    }

    /// <summary>Фильтр по карте: у каждой карты свои зоны и свои привычки игрока, профиль строится по одной карте.</summary>
    private void FillMaps()
    {
        var previous = SelectedMap?.MapName;
        _filling = true;
        Maps.Clear();
        if (SelectedPlayer is { } player)
        {
            foreach (var map in PlayerEntries(player.SteamId).GroupBy(e => e.MapName, StringComparer.OrdinalIgnoreCase)
                         .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                Maps.Add(new LibraryMapOption(map.Key, map.Count(), map.Sum(e => e.RoundCount)));
        }
        _filling = false;

        SelectedMap = Maps.FirstOrDefault(m => string.Equals(m.MapName, previous, StringComparison.OrdinalIgnoreCase)) ?? Maps.FirstOrDefault();
        if (string.Equals(SelectedMap?.MapName, previous, StringComparison.OrdinalIgnoreCase))
            FillDemos();
    }

    partial void OnSelectedMapChanged(LibraryMapOption? value)
    {
        if (!_filling)
            FillDemos();
    }

    private void FillDemos()
    {
        foreach (var row in Demos)
            row.PropertyChanged -= OnDemoChanged;
        Demos.Clear();
        if (SelectedPlayer is { } player && SelectedMap is { } map)
        {
            foreach (var entry in PlayerEntries(player.SteamId).Where(e => string.Equals(e.MapName, map.MapName, StringComparison.OrdinalIgnoreCase)))
            {
                var row = new LibraryDemoRowViewModel(entry, entry.FindPlayer(player.SteamId)!);
                row.PropertyChanged += OnDemoChanged;
                Demos.Add(row);
            }
        }
        OnSelectionChanged();
    }

    private IEnumerable<LibraryEntry> PlayerEntries(ulong steamId) =>
        _entries.Where(e => e.IsCurrent && e.FindPlayer(steamId) is not null).OrderByDescending(e => e.FileTimeUtc);

    private void SetIncluded(bool value)
    {
        foreach (var row in Demos)
            row.IsIncluded = value;
    }

    private void OnDemoChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LibraryDemoRowViewModel.IsIncluded))
            OnSelectionChanged();
    }

    private void OnSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(SelectionHint));
        BuildProfileCommand.NotifyCanExecuteChanged();
    }
}

public sealed record LibraryFolderViewModel(string Path, int DemoCount, bool Exists)
{
    public string CountText => Exists
        ? Loc.T($"демок в библиотеке: {DemoCount}", $"demos in the library: {DemoCount}")
        : Loc.T("папка недоступна", "folder unavailable");
}

/// <summary>Игрок в выборе сводного профиля.</summary>
public sealed record LibraryPlayerOption(ulong SteamId, string Name, int DemoCount, bool IsMe)
{
    public string Initials => Texts.Initials(Name);

    public string CountText => Loc.T($"демок: {DemoCount}", $"demos: {DemoCount}");
}

/// <summary>Карта в фильтре: «de_mirage · 5».</summary>
public sealed record LibraryMapOption(string MapName, int DemoCount, int RoundCount)
{
    public string CountText => $"{DemoCount}";

    public string Tooltip => Loc.T($"демок: {DemoCount}, раундов: {RoundCount}", $"demos: {DemoCount}, rounds: {RoundCount}");
}

/// <summary>Демка игрока в библиотеке: отметка «включить в профиль», дата, счёт с его стороны, K/D/A.</summary>
public partial class LibraryDemoRowViewModel(LibraryEntry entry, LibraryPlayer player) : ViewModelBase
{
    public LibraryEntry Entry { get; } = entry;

    [ObservableProperty]
    public partial bool IsIncluded { get; set; } = true;

    public string FileName => Entry.FileName;

    public string DateText => Entry.FileTimeUtc.ToLocalTime().ToString("g", Loc.Culture);

    /// <summary>Счёт с точки зрения команды игрока (сторона на конец матча).</summary>
    public string ScoreText => $"{Own} : {Other}";

    private int Own => player.FinalSide == Side.CT ? Entry.Score.TeamCTScore : Entry.Score.TeamTScore;

    private int Other => player.FinalSide == Side.CT ? Entry.Score.TeamTScore : Entry.Score.TeamCTScore;

    public bool IsWin => Own > Other;

    public bool IsLoss => Own < Other;

    public string KdaText => $"{player.Kills} / {player.Deaths} / {player.Assists}";

    public string RoundsText => Loc.T($"раундов: {Entry.RoundCount}", $"rounds: {Entry.RoundCount}");

    public bool FileMissing => !File.Exists(Entry.FullPath);

    public string Tooltip => Entry.FullPath + (FileMissing
        ? Loc.T(" — файла нет, анализ сохранён в библиотеке", " — the file is gone, the analysis is kept in the library")
        : "");
}
