using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemReview.Application;
using DemReview.Application.Context;
using DemReview.Application.Export;
using DemReview.Application.Patterns;
using DemReview.Application.Situations;
using DemReview.Desktop.Services;
using DemReview.Domain;
using Microsoft.Extensions.Logging;

namespace DemReview.Desktop.ViewModels;

/// <summary>Вкладка «Профиль игрока»: роль, паттерны, сводка по сторонам, предсказуемость, экспорт.</summary>
public partial class ProfileViewModel(
    IPlayerPatternService patterns,
    IProfileExporter exporter,
    ISettingsService settings,
    IFileDialogService fileDialog,
    ILogger<ProfileViewModel> logger) : ViewModelBase
{
    private MatchData? _match;
    private ulong? _steamId;
    private int _version;

    /// <summary>Запрос показать раунды-примеры паттерна на радаре.</summary>
    public event Action<Pattern, bool>? ShowOnRadarRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProfile))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    public partial PlayerProfile? Profile { get; private set; }

    [ObservableProperty]
    public partial bool IsBuilding { get; private set; }

    [ObservableProperty]
    public partial PatternRowViewModel? SelectedPattern { get; set; }

    [ObservableProperty]
    public partial FilterOption SelectedSort { get; set; } = SortModes[0];

    [ObservableProperty]
    public partial decimal MinSharePercent { get; set; } = (decimal)Math.Round(settings.Current.PatternMinShare * 100);

    [ObservableProperty]
    public partial decimal MinExamples { get; set; } = settings.Current.PatternMinExamples;

    [ObservableProperty]
    public partial string? StatusText { get; private set; }

    public static IReadOnlyList<FilterOption> SortModes { get; } =
    [
        new("по частоте", "share"),
        new("по уверенности", "confidence"),
        new("по категории", "category"),
    ];

    public ObservableCollection<PatternRowViewModel> Patterns { get; } = [];

    public ObservableCollection<RoleRowViewModel> Roles { get; } = [];

    public ObservableCollection<SideSummaryRowViewModel> Sides { get; } = [];

    public bool HasProfile => Profile is not null;

    public string Title => Profile is { } p ? $"{p.Name} · {p.MapName}" : "";

    public int Predictability => Profile?.PredictabilityPercent ?? 0;

    public string PredictabilityText => Profile is not { } p ? ""
        : $"{p.PredictabilityPercent}% — {(p.PredictabilityPercent >= 70 ? "очень предсказуем" : p.PredictabilityPercent >= 50 ? "умеренно предсказуем" : "непредсказуем")}: " +
          "как часто в похожих ситуациях (сторона + закупка) он выбирает один и тот же маршрут, позицию и стиль.";

    public string PatternsHeader => Profile is not { } p ? ""
        : $"Паттерны: {p.Patterns.Count} (частота ≥ {p.Options.MinShare:P0}, примеров ≥ {p.Options.MinExamples}, раундов в анализе: {p.RoundsAnalyzed})";

    partial void OnProfileChanged(PlayerProfile? value)
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Predictability));
        OnPropertyChanged(nameof(PredictabilityText));
        OnPropertyChanged(nameof(PatternsHeader));
    }

    partial void OnSelectedSortChanged(FilterOption value) => FillPatterns();

    partial void OnSelectedPatternChanged(PatternRowViewModel? value)
    {
        if (value is not null)
            ShowOnRadarRequested?.Invoke(value.Pattern, false);
    }

    partial void OnMinSharePercentChanged(decimal value) => _ = ApplyThresholdsAsync();

    partial void OnMinExamplesChanged(decimal value) => _ = ApplyThresholdsAsync();

    public void SetMatch(MatchData match)
    {
        _match = match;
        _steamId = null;
        Clear();
    }

    /// <summary>Построение профиля в фоновом потоке.</summary>
    public async Task ShowPlayerAsync(ulong? steamId)
    {
        var version = ++_version;
        _steamId = steamId;
        if (_match is not { } match || steamId is not { } id)
        {
            Clear();
            return;
        }

        IsBuilding = true;
        try
        {
            var options = Options();
            var profile = await Task.Run(() => patterns.BuildProfile(match, id, options));
            if (version != _version)
                return;

            Profile = profile;
            Roles.Clear();
            foreach (var role in profile.Roles)
                Roles.Add(new RoleRowViewModel(role));
            Sides.Clear();
            foreach (var side in profile.Sides)
                Sides.Add(new SideSummaryRowViewModel(side));
            FillPatterns();
        }
        finally
        {
            if (version == _version)
                IsBuilding = false;
        }
    }

    [RelayCommand]
    private void ShowOnRadar(PatternRowViewModel? row)
    {
        if (row is not null)
            ShowOnRadarRequested?.Invoke(row.Pattern, true);
    }

    /// <summary>Экспорт профиля в Markdown или JSON (по выбранному типу файла).</summary>
    [RelayCommand(CanExecute = nameof(HasProfile))]
    private async Task ExportAsync()
    {
        if (Profile is not { } profile || _match is not { } match)
            return;

        var suggested = SafeFileName($"Профиль {profile.Name} {profile.MapName}") + ".md";
        if (await fileDialog.PickProfileSavePathAsync(suggested) is not { } path)
            return;

        try
        {
            var format = exporter.FormatFor(path);
            var text = await Task.Run(() => exporter.Export(profile, match, format));
            await File.WriteAllTextAsync(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            StatusText = $"Профиль сохранён: {path}";
            logger.LogInformation("Профиль {Name} экспортирован в {Path}", profile.Name, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Не удалось сохранить профиль в {Path}", path);
            StatusText = $"Не удалось сохранить файл: {ex.Message}";
        }
    }

    private async Task ApplyThresholdsAsync()
    {
        var share = (double)Math.Clamp(MinSharePercent, 1, 100) / 100;
        var examples = (int)Math.Clamp(MinExamples, 1, 50);
        settings.Update(s => s with { PatternMinShare = share, PatternMinExamples = examples });
        await ShowPlayerAsync(_steamId);
    }

    private PatternOptions Options()
    {
        var s = settings.Current;
        return new PatternOptions(s.PatternMinShare, s.PatternMinExamples,
            new EconomyOptions(s.BuyEcoMaxEquipment, s.BuyFullMinEquipment),
            s.AggressiveContactSeconds, s.PassiveContactSeconds, s.GrenadeClusterThrowDistance, s.GrenadeClusterLandDistance);
    }

    private void FillPatterns()
    {
        Patterns.Clear();
        if (Profile is not { } profile)
            return;

        IEnumerable<Pattern> ordered = SelectedSort.Value switch
        {
            "confidence" => profile.Patterns.OrderByDescending(p => p.ConfidenceScore).ThenByDescending(p => p.Share),
            "category" => profile.Patterns.OrderBy(p => p.Category).ThenBy(p => p.Side).ThenByDescending(p => p.Share),
            _ => profile.Patterns.OrderByDescending(p => p.Share).ThenByDescending(p => p.ConfidenceScore),
        };
        foreach (var pattern in ordered)
            Patterns.Add(new PatternRowViewModel(pattern));
    }

    private void Clear()
    {
        Profile = null;
        Patterns.Clear();
        Roles.Clear();
        Sides.Clear();
        StatusText = null;
    }

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['\\', '/', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
    }
}

public sealed class PatternRowViewModel(Pattern pattern)
{
    public Pattern Pattern { get; } = pattern;

    public string Text => Pattern.Text;

    public string Details =>
        $"{Category(Pattern.Category)} · {Pattern.Count} из {Pattern.Total} ({Pattern.Share:P0}) · уверенность: {ProfileExporter.ConfidenceName(Pattern.Confidence)} · " +
        $"раунды: {string.Join(", ", Pattern.Rounds)}";

    public bool IsHighConfidence => Pattern.Confidence == Confidence.High;

    public bool IsLowConfidence => Pattern.Confidence == Confidence.Low;

    public static string Category(PatternCategory category) => category switch
    {
        PatternCategory.Route => "маршрут",
        PatternCategory.Position => "позиция",
        PatternCategory.Timing => "тайминг",
        PatternCategory.Aggression => "агрессия",
        PatternCategory.Rotation => "ротация",
        PatternCategory.Reaction => "реакция",
        PatternCategory.Grenades => "гранаты",
        _ => "смерти",
    };
}

public sealed class RoleRowViewModel(RoleAssessment role)
{
    public string Header => $"{Labels.Side(role.Side)}: {ProfileExporter.RoleName(role.Role)}";

    public string Confidence => $"уверенность: {ProfileExporter.ConfidenceName(role.Confidence)}";

    public string Reasons => string.Join(" · ", role.Reasons);
}

public sealed class SideSummaryRowViewModel(SideMetrics m)
{
    public string Side => Labels.Side(m.Side);
    public int Rounds => m.Rounds;
    public string KillsDeaths => $"{m.Kills} / {m.Deaths}";
    public string Survival => $"{m.SurvivalShare:P0}";
    public string Wins => $"{m.RoundWinShare:P0}";
    public string Contact => m.MedianContactTime is { } c ? Labels.Time(c) : "—";
    public string FirstDuel => $"{m.FirstDuelShare:P0} (выигр. {m.FirstDuelWinShare:P0})";
    public string Grenades => $"{m.GrenadesPerRound:F1}";
    public string Style => $"{m.AggressiveShare:P0} / {m.PassiveShare:P0}";
    public string Distance => $"{m.MedianTeammateDistance:F0}";
}
