using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemReview.Application;
using DemReview.Application.Context;
using DemReview.Application.Export;
using DemReview.Application.Localization;
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
        new("по частоте", "by frequency", "share"),
        new("по уверенности", "by confidence", "confidence"),
        new("по категории", "by category", "category"),
    ];

    public ObservableCollection<PatternRowViewModel> Patterns { get; } = [];

    public ObservableCollection<RoleRowViewModel> Roles { get; } = [];

    public ObservableCollection<SideSummaryRowViewModel> Sides { get; } = [];

    public bool HasProfile => Profile is not null;

    public string Title => Profile is { } p ? $"{p.Name} · {p.MapName}" : "";

    public int Predictability => Profile?.PredictabilityPercent ?? 0;

    public string PredictabilityText => Profile is not { } p ? ""
        : $"{p.PredictabilityPercent}% — " + (p.PredictabilityPercent >= 70 ? Loc.T("очень предсказуем", "very predictable")
            : p.PredictabilityPercent >= 50 ? Loc.T("умеренно предсказуем", "moderately predictable") : Loc.T("непредсказуем", "unpredictable")) +
          Loc.T(": как часто в похожих ситуациях (сторона + закупка) игрок выбирает один и тот же маршрут, позицию и стиль.",
              ": how often the player picks the same route, position and style in similar situations (side + buy).");

    public string PatternsHeader => Profile is not { } p ? ""
        : Loc.T($"Паттерны: {p.Patterns.Count} (частота ≥ {p.Options.MinShare:P0}, примеров ≥ {p.Options.MinExamples}, раундов в анализе: {p.RoundsAnalyzed})",
            $"Patterns: {p.Patterns.Count} (frequency ≥ {p.Options.MinShare:P0}, examples ≥ {p.Options.MinExamples}, rounds analyzed: {p.RoundsAnalyzed})");

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

        var suggested = SafeFileName(Loc.T($"Профиль {profile.Name} {profile.MapName}", $"Profile {profile.Name} {profile.MapName}")) + ".md";
        if (await fileDialog.PickProfileSavePathAsync(suggested) is not { } path)
            return;

        try
        {
            var format = exporter.FormatFor(path);
            var text = await Task.Run(() => exporter.Export(profile, match, format));
            await File.WriteAllTextAsync(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            StatusText = Loc.T($"Профиль сохранён: {path}", $"Profile saved: {path}");
            logger.LogInformation("Профиль {Name} экспортирован в {Path}", profile.Name, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Не удалось сохранить профиль в {Path}", path);
            StatusText = Loc.T($"Не удалось сохранить файл: {ex.Message}", $"Could not save the file: {ex.Message}");
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

    public string Details => Loc.T(
        $"{Category(Pattern.Category)} · {Pattern.Count} из {Pattern.Total} ({Pattern.Share:P0}) · уверенность: {ProfileExporter.ConfidenceName(Pattern.Confidence)} · " +
        $"раунды: {string.Join(", ", Pattern.Rounds)}",
        $"{Category(Pattern.Category)} · {Pattern.Count} of {Pattern.Total} ({Pattern.Share:P0}) · confidence: {ProfileExporter.ConfidenceName(Pattern.Confidence)} · " +
        $"rounds: {string.Join(", ", Pattern.Rounds)}");

    public bool IsHighConfidence => Pattern.Confidence == Confidence.High;

    public bool IsLowConfidence => Pattern.Confidence == Confidence.Low;

    public static string Category(PatternCategory category) => category switch
    {
        PatternCategory.Route => Loc.T("маршрут", "route"),
        PatternCategory.Position => Loc.T("позиция", "position"),
        PatternCategory.Timing => Loc.T("тайминг", "timing"),
        PatternCategory.Aggression => Loc.T("агрессия", "aggression"),
        PatternCategory.Rotation => Loc.T("ротация", "rotation"),
        PatternCategory.Reaction => Loc.T("реакция", "reaction"),
        PatternCategory.Grenades => Loc.T("гранаты", "grenades"),
        _ => Loc.T("смерти", "deaths"),
    };
}

public sealed class RoleRowViewModel(RoleAssessment role)
{
    public string Header => $"{Labels.Side(role.Side)}: {ProfileExporter.RoleName(role.Role)}";

    public string Confidence => Loc.T($"уверенность: {ProfileExporter.ConfidenceName(role.Confidence)}", $"confidence: {ProfileExporter.ConfidenceName(role.Confidence)}");

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
    public string FirstDuel => $"{m.FirstDuelShare:P0} ({Loc.T("выигр.", "won")} {m.FirstDuelWinShare:P0})";
    public string Damage => $"{m.DamagePerRound:F0}";
    public string Grenades => $"{m.GrenadesPerRound:F1}";
    public string Style => $"{m.AggressiveShare:P0} / {m.PassiveShare:P0}";
    public string Distance => $"{m.MedianTeammateDistance:F0}";
}
