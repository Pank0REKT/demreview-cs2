using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemReview.Application;
using DemReview.Application.Localization;

namespace DemReview.Desktop.ViewModels;

/// <summary>Пороги анализа из settings.json: правятся во вкладке, применяются при повторном анализе (F5).</summary>
public partial class SettingsViewModel
{
    /// <summary>Изменены пороги паттернов (частота, примеры) — профиль подтягивает их сразу.</summary>
    public event Action? PatternThresholdsChanged;

    private ObservableCollection<ThresholdViewModel>? _thresholds;

    public ObservableCollection<ThresholdViewModel> Thresholds => _thresholds ??= [.. CreateThresholds()];

    private IEnumerable<ThresholdViewModel> CreateThresholds()
    {
        ThresholdViewModel Create(string label, string key, string unit, decimal min, decimal max, decimal step,
            Func<AppSettings, decimal> get, Func<AppSettings, decimal, AppSettings> set, bool pattern = false) =>
            new(label, key, unit, min, max, step, get(_settings.Current), get(new AppSettings()), value =>
            {
                _settings.Update(s => set(s, value));
                if (pattern)
                    PatternThresholdsChanged?.Invoke();
            });

        yield return Create(Loc.T("Эко — экипировка до", "Eco — equipment below"), nameof(AppSettings.BuyEcoMaxEquipment), "$", 0, 10000, 100,
            s => s.BuyEcoMaxEquipment, (s, v) => s with { BuyEcoMaxEquipment = (int)v });
        yield return Create(Loc.T("Фулл-бай — экипировка от", "Full buy — equipment from"), nameof(AppSettings.BuyFullMinEquipment), "$", 0, 10000, 100,
            s => s.BuyFullMinEquipment, (s, v) => s with { BuyFullMinEquipment = (int)v });
        yield return Create(Loc.T("Агрессивный раунд — бой до", "Aggressive round — fight before"), nameof(AppSettings.AggressiveContactSeconds),
            Loc.T("с", "s"), 1, 115, 1, s => (decimal)s.AggressiveContactSeconds, (s, v) => s with { AggressiveContactSeconds = (float)v });
        yield return Create(Loc.T("Пассивный раунд — бой после", "Passive round — fight after"), nameof(AppSettings.PassiveContactSeconds),
            Loc.T("с", "s"), 1, 115, 1, s => (decimal)s.PassiveContactSeconds, (s, v) => s with { PassiveContactSeconds = (float)v });
        yield return Create(Loc.T("Минимальная частота паттерна", "Minimum pattern frequency"), nameof(AppSettings.PatternMinShare), "%", 10, 100, 5,
            s => (decimal)Math.Round(s.PatternMinShare * 100), (s, v) => s with { PatternMinShare = (double)v / 100 }, pattern: true);
        yield return Create(Loc.T("Минимум раундов-примеров", "Minimum example rounds"), nameof(AppSettings.PatternMinExamples), "", 1, 20, 1,
            s => s.PatternMinExamples, (s, v) => s with { PatternMinExamples = (int)v }, pattern: true);
        yield return Create(Loc.T("Шаг выборки позиций", "Position sampling step"), nameof(AppSettings.SampleTickStep), Loc.T("тиков", "ticks"), 1, 128, 1,
            s => s.SampleTickStep, (s, v) => s with { SampleTickStep = (int)v });
        yield return Create(Loc.T("Раскидка: точки броска", "Lineup: throw points"), nameof(AppSettings.GrenadeClusterThrowDistance), "u", 10, 2000, 10,
            s => (decimal)s.GrenadeClusterThrowDistance, (s, v) => s with { GrenadeClusterThrowDistance = (float)v });
        yield return Create(Loc.T("Раскидка: точки взрыва", "Lineup: detonation points"), nameof(AppSettings.GrenadeClusterLandDistance), "u", 10, 2000, 10,
            s => (decimal)s.GrenadeClusterLandDistance, (s, v) => s with { GrenadeClusterLandDistance = (float)v });
    }

    [RelayCommand]
    private void ResetThresholds()
    {
        foreach (var threshold in Thresholds)
            threshold.Value = threshold.Default;
    }
}

/// <summary>Порог анализа: подпись, ключ в settings.json, единица и значение.</summary>
public sealed partial class ThresholdViewModel(
    string label, string key, string unit, decimal minimum, decimal maximum, decimal increment,
    decimal value, decimal defaultValue, Action<decimal> save) : ObservableObject
{
    public string Label { get; } = label;

    public string Key { get; } = key;

    public string Unit { get; } = unit;

    public decimal Minimum { get; } = minimum;

    public decimal Maximum { get; } = maximum;

    public decimal Increment { get; } = increment;

    public decimal Default { get; } = defaultValue;

    [ObservableProperty]
    public partial decimal? Value { get; set; } = value;

    partial void OnValueChanged(decimal? value)
    {
        if (value is { } v)
            save(Math.Clamp(v, Minimum, Maximum));
    }
}
