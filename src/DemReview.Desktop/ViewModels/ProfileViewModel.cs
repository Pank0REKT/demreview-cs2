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

    /// <summary>Карточки ролей по сторонам с ключевыми метриками.</summary>
    public ObservableCollection<RoleCardViewModel> RoleCards { get; } = [];

    /// <summary>Сравнение метрик T и CT.</summary>
    public ObservableCollection<CompareRowViewModel> Comparison { get; } = [];

    /// <summary>Фильтр паттернов по категории: «Все» и категории, которые есть в профиле.</summary>
    public ObservableCollection<FilterOption> Categories { get; } = [];

    [ObservableProperty]
    public partial FilterOption? SelectedCategory { get; set; }

    partial void OnSelectedCategoryChanged(FilterOption? value) => FillPatterns();

    public bool HasProfile => Profile is not null;

    public string Name => Profile?.Name ?? "";

    public string Initials => Texts.Initials(Name);

    public bool IsMe => Profile is { } p && p.SteamId == settings.Current.MainPlayerSteamId;

    /// <summary>«de_cache · team_Pank0 · раундов: 22 · 26 / 11 / 3».</summary>
    public string Subtitle => Profile is not { } p || _match?.FindPlayer(p.SteamId) is not { } player ? ""
        : $"{p.MapName} · {ProfileExporter.TeamName(player.TeamName, player.FinalSide)} · {Loc.T("раундов", "rounds")}: {p.RoundsAnalyzed} · " +
          $"{player.Kills} / {player.Deaths} / {player.Assists}";

    /// <summary>Дуга кольца предсказуемости, градусы.</summary>
    public double PredictabilityAngle => Predictability * 3.6;

    public string PredictabilityLabel => Predictability >= 70 ? Loc.T("Очень предсказуем", "Very predictable")
        : Predictability >= 50 ? Loc.T("Умеренно предсказуем", "Moderately predictable") : Loc.T("Непредсказуем", "Unpredictable");

    public static string PredictabilityHint => Loc.T("Как часто в похожих ситуациях (сторона + закупка) выбирает тот же маршрут, позицию и стиль.",
        "How often the player picks the same route, position and style in similar situations (side + buy).");

    public int PatternCount => Profile?.Patterns.Count ?? 0;

    /// <summary>Основной игрок сменился — обновить отметку «Вы».</summary>
    public void RefreshMainPlayer() => OnPropertyChanged(nameof(IsMe));

    /// <summary>Пороги изменили в настройках — подтянуть значения и пересобрать профиль.</summary>
    public void ReloadThresholds()
    {
        MinSharePercent = (decimal)Math.Round(settings.Current.PatternMinShare * 100);
        MinExamples = settings.Current.PatternMinExamples;
    }

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
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Initials));
        OnPropertyChanged(nameof(IsMe));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(PredictabilityAngle));
        OnPropertyChanged(nameof(PredictabilityLabel));
        OnPropertyChanged(nameof(PatternCount));
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
            RoleCards.Clear();
            foreach (var role in profile.Roles)
                RoleCards.Add(new RoleCardViewModel(role, profile.Sides.FirstOrDefault(s => s.Side == role.Side)));
            FillComparison(profile);
            FillCategories(profile);
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

        var category = SelectedCategory?.Value as PatternCategory?;
        IEnumerable<Pattern> ordered = SelectedSort.Value switch
        {
            "confidence" => profile.Patterns.OrderByDescending(p => p.ConfidenceScore).ThenByDescending(p => p.Share),
            "category" => profile.Patterns.OrderBy(p => p.Category).ThenBy(p => p.Side).ThenByDescending(p => p.Share),
            _ => profile.Patterns.OrderByDescending(p => p.Share).ThenByDescending(p => p.ConfidenceScore),
        };
        foreach (var pattern in ordered.Where(p => category is null || p.Category == category))
            Patterns.Add(new PatternRowViewModel(pattern));
    }

    /// <summary>Категории паттернов профиля; выбранная сохраняется, если она есть и в новом профиле.</summary>
    private void FillCategories(PlayerProfile profile)
    {
        var previous = SelectedCategory?.Value as PatternCategory?;
        Categories.Clear();
        Categories.Add(new FilterOption("Все", "All", null));
        foreach (var category in profile.Patterns.Select(p => p.Category).Distinct().Order())
            Categories.Add(new FilterOption(() => PatternRowViewModel.CategoryTitle(category), category));
        SelectedCategory = Categories.FirstOrDefault(c => Equals(c.Value, previous)) ?? Categories[0];
    }

    private void FillComparison(PlayerProfile profile)
    {
        Comparison.Clear();
        if (profile.Sides.FirstOrDefault(s => s.Side == Side.T) is not { } t || profile.Sides.FirstOrDefault(s => s.Side == Side.CT) is not { } ct)
            return;

        void Add(string label, string tText, string ctText, double tValue, double ctValue) =>
            Comparison.Add(new CompareRowViewModel(label, tText, ctText, tValue, ctValue));

        Add(Loc.T("Урон за раунд", "Damage per round"), $"{t.DamagePerRound:F0}", $"{ct.DamagePerRound:F0}", t.DamagePerRound, ct.DamagePerRound);
        Add(Loc.T("Выживание", "Survival"), $"{t.SurvivalShare:P0}", $"{ct.SurvivalShare:P0}", t.SurvivalShare, ct.SurvivalShare);
        Add(Loc.T("Победы", "Wins"), $"{t.RoundWinShare:P0}", $"{ct.RoundWinShare:P0}", t.RoundWinShare, ct.RoundWinShare);
        Add(Loc.T("У / С", "K / D"), $"{t.Kills} / {t.Deaths}", $"{ct.Kills} / {ct.Deaths}", t.Kills, ct.Kills);
        Add(Loc.T("Первый контакт", "First contact"), Contact(t), Contact(ct), t.MedianContactTime ?? 0, ct.MedianContactTime ?? 0);
        Add(Loc.T("Первая дуэль", "Opening duel"), $"{t.FirstDuelShare:P0}", $"{ct.FirstDuelShare:P0}", t.FirstDuelShare, ct.FirstDuelShare);
        Add(Loc.T("Гранат за раунд", "Grenades per round"), $"{t.GrenadesPerRound:F1}", $"{ct.GrenadesPerRound:F1}", t.GrenadesPerRound, ct.GrenadesPerRound);
        Add(Loc.T("Агрессия / пассив", "Aggressive / passive"), $"{t.AggressiveShare:P0} / {t.PassiveShare:P0}", $"{ct.AggressiveShare:P0} / {ct.PassiveShare:P0}",
            t.AggressiveShare, ct.AggressiveShare);
        Add(Loc.T("До союзника", "To teammate"), $"{t.MedianTeammateDistance:F0}u", $"{ct.MedianTeammateDistance:F0}u", t.MedianTeammateDistance, ct.MedianTeammateDistance);
        Add(Loc.T("Раундов", "Rounds"), $"{t.Rounds}", $"{ct.Rounds}", t.Rounds, ct.Rounds);
    }

    private static string Contact(SideMetrics m) => m.MedianContactTime is { } c ? Labels.Time(c) : "—";

    private void Clear()
    {
        Profile = null;
        Patterns.Clear();
        Roles.Clear();
        Sides.Clear();
        RoleCards.Clear();
        Comparison.Clear();
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

    public string SideText => Labels.Side(Pattern.Side);

    public bool IsT => Pattern.Side == Side.T;

    public string CategoryText => CategoryTitle(Pattern.Category);

    public string ConfidenceText => ProfileExporter.ConfidenceName(Pattern.Confidence);

    public string CountText => $"{Pattern.Count}";

    public string TotalText => Loc.T($"из {Pattern.Total}", $"of {Pattern.Total}");

    public string PercentText => $"{Pattern.Share:P0}";

    public double Share => Pattern.Share;

    public string RoundsText => string.Join(", ", Pattern.Rounds);

    /// <summary>Категория с заглавной буквы — для бейджей и фильтра.</summary>
    public static string CategoryTitle(PatternCategory category)
    {
        var text = Category(category);
        return text.Length == 0 ? text : char.ToUpper(text[0], System.Globalization.CultureInfo.CurrentCulture) + text[1..];
    }

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

/// <summary>Карточка роли на стороне: название, пояснение, уверенность и ключевые метрики.</summary>
public sealed class RoleCardViewModel(RoleAssessment role, SideMetrics? m)
{
    private readonly string[] _name = ProfileExporter.RoleName(role.Role).Split(" — ", 2);

    public string SideText => Labels.Side(role.Side);

    public bool IsT => role.Side == Side.T;

    /// <summary>«Support».</summary>
    public string RoleTitle => _name[0].Length == 0 ? "" : char.ToUpperInvariant(_name[0][0]) + _name[0][1..];

    /// <summary>«гранаты и поддержка».</summary>
    public string RoleHint => _name.Length > 1 ? _name[1] : "";

    public string ConfidenceText => Loc.T($"{ProfileExporter.ConfidenceName(role.Confidence)} уверенность", $"{ProfileExporter.ConfidenceName(role.Confidence)} confidence");

    public Confidence Confidence => role.Confidence;

    public string Reasons => string.Join(" · ", role.Reasons);

    public IReadOnlyList<MetricTile> Metrics { get; } = m is null ? [] :
    [
        new(Loc.T("Урон / раунд", "Damage / round"), $"{m.DamagePerRound:F0}"),
        new(Loc.T("Гранат / раунд", "Grenades / round"), $"{m.GrenadesPerRound:F1}"),
        new(Loc.T("Первый контакт", "First contact"), m.MedianContactTime is { } c ? $"~{Labels.Time(c)}" : "—"),
        new(Loc.T("Первая дуэль", "Opening duel"), $"{m.FirstDuelShare:P0}"),
        new(Loc.T("До союзника", "To teammate"), $"{m.MedianTeammateDistance:F0}u"),
        new(Loc.T("Одна позиция", "Holds one spot"), $"{m.AnchorShare:P0}"),
    ];
}

public sealed record MetricTile(string Label, string Value);

/// <summary>Строка сравнения T и CT: значения и доли полос (большее значение — полная полоса).</summary>
public sealed class CompareRowViewModel(string label, string tText, string ctText, double tValue, double ctValue)
{
    private readonly double _max = Math.Max(Math.Max(tValue, ctValue), 1e-9);

    public string Label => label;

    public string TText => tText;

    public string CtText => ctText;

    public double TFraction => Math.Max(0, tValue) / _max;

    public double CtFraction => Math.Max(0, ctValue) / _max;
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
