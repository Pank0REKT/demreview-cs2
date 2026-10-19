using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemReview.Application.Filters;
using DemReview.Application.Situations;
using DemReview.Domain;

namespace DemReview.Desktop.ViewModels;

/// <summary>Вариант значения фильтра для ComboBox.</summary>
public sealed record FilterOption(string Label, object? Value)
{
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

    public ObservableCollection<FilterOption> Rounds { get; } = [new("Все раунды", null)];

    public IReadOnlyList<FilterOption> Sides { get; } =
    [
        new("Обе стороны", null),
        new("T", Side.T),
        new("CT", Side.CT),
    ];

    public IReadOnlyList<FilterOption> Buys { get; } =
    [
        new("Любая закупка", null),
        .. Enum.GetValues<BuyType>().Select(b => new FilterOption(Labels.Buy(b), b)),
    ];

    public IReadOnlyList<FilterOption> Grenades { get; } =
    [
        new("Все гранаты", null),
        new("HE", GrenadeFilterGroup.HighExplosive),
        new("Флешки", GrenadeFilterGroup.Flash),
        new("Дым", GrenadeFilterGroup.Smoke),
        new("Огонь (молотов, зажигательная)", GrenadeFilterGroup.Fire),
        new("Ложные", GrenadeFilterGroup.Decoy),
    ];

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
        Rounds.Add(new FilterOption("Все раунды", null));
        foreach (var round in match.Rounds)
            Rounds.Add(new FilterOption($"Раунд {round.Number}", round.Number));
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
