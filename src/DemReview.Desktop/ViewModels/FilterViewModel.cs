using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemReview.Application.Filters;
using DemReview.Application.Localization;
using DemReview.Application.Situations;
using DemReview.Domain;

namespace DemReview.Desktop.ViewModels;

/// <summary>Вариант значения для ComboBox. Подпись вычисляется при показе — на текущем языке.</summary>
public sealed class FilterOption(Func<string> label, object? value)
{
    public FilterOption(string ru, string en, object? value)
        : this(() => Loc.T(ru, en), value)
    {
    }

    public string Label => label();

    public object? Value { get; } = value;

    /// <summary>Вариант стороны T или CT — подпись в цвете стороны.</summary>
    public bool IsT => Value is Side.T;

    public bool IsCT => Value is Side.CT;

    public override string ToString() => Label;
}

/// <summary>Фильтры слева: раунд, сторона, тип закупки команды, тип гранаты.</summary>
public partial class FilterViewModel : ViewModelBase
{
    public FilterViewModel()
    {
        SelectedRound = Rounds[0];
        SelectedSide = Sides[0];
        SelectedBuy = Buys[0];
        SelectedGrenade = Grenades[0];
    }

    /// <summary>Срабатывает при любом изменении фильтра.</summary>
    public event Action<AnalysisFilter>? Changed;

    public ObservableCollection<FilterOption> Rounds { get; } = [AllRounds()];

    public IReadOnlyList<FilterOption> Sides { get; } =
    [
        new("Обе стороны", "Both sides", null),
        new(() => "T", Side.T),
        new(() => "CT", Side.CT),
    ];

    public IReadOnlyList<FilterOption> Buys { get; } =
    [
        new("Любая закупка", "Any buy", null),
        .. Enum.GetValues<BuyType>().Select(b => new FilterOption(() => Labels.Buy(b), b)),
    ];

    public IReadOnlyList<FilterOption> Grenades { get; } =
    [
        new("Все гранаты", "All grenades", null),
        new(() => "HE", GrenadeFilterGroup.HighExplosive),
        new("Флешки", "Flashbangs", GrenadeFilterGroup.Flash),
        new("Дым", "Smokes", GrenadeFilterGroup.Smoke),
        new("Огонь (молотов, зажигательная)", "Fire (molotov, incendiary)", GrenadeFilterGroup.Fire),
        new("Ложные", "Decoys", GrenadeFilterGroup.Decoy),
    ];

    private static FilterOption AllRounds() => new("Все раунды", "All rounds", null);

    [ObservableProperty]
    public partial FilterOption? SelectedRound { get; set; }

    [ObservableProperty]
    public partial FilterOption? SelectedSide { get; set; }

    [ObservableProperty]
    public partial FilterOption? SelectedBuy { get; set; }

    [ObservableProperty]
    public partial FilterOption? SelectedGrenade { get; set; }

    public AnalysisFilter Current => new(
        SelectedRound?.Value as int?,
        SelectedSide?.Value as Side?,
        SelectedBuy?.Value as BuyType?,
        SelectedGrenade?.Value as GrenadeFilterGroup?);

    public bool IsActive => !Current.IsEmpty;

    partial void OnSelectedRoundChanged(FilterOption? value) => RaiseChanged();
    partial void OnSelectedSideChanged(FilterOption? value) => RaiseChanged();
    partial void OnSelectedBuyChanged(FilterOption? value) => RaiseChanged();
    partial void OnSelectedGrenadeChanged(FilterOption? value) => RaiseChanged();

    /// <summary>Новая демка: список раундов, фильтры сбрасываются.</summary>
    public void SetMatch(MatchData match)
    {
        Rounds.Clear();
        Rounds.Add(AllRounds());
        foreach (var round in match.Rounds)
            Rounds.Add(new FilterOption($"Раунд {round.Number}", $"Round {round.Number}", round.Number));
        Reset();
    }

    /// <summary>Выбор раунда извне (например, из списка маршрутов).</summary>
    public void SelectRound(int? round) =>
        SelectedRound = Rounds.FirstOrDefault(r => Equals(r.Value, round)) ?? Rounds[0];

    [RelayCommand]
    private void Reset()
    {
        _suspend = true;
        SelectedRound = Rounds[0];
        SelectedSide = Sides[0];
        SelectedBuy = Buys[0];
        SelectedGrenade = Grenades[0];
        _suspend = false;
        RaiseChanged();
    }

    private bool _suspend;

    private void RaiseChanged()
    {
        if (_suspend)
            return;
        OnPropertyChanged(nameof(IsActive));
        Changed?.Invoke(Current);
    }
}
