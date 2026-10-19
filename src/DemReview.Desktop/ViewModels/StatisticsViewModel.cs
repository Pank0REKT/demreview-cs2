using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DemReview.Application;
using DemReview.Application.Context;
using DemReview.Application.Situations;
using DemReview.Domain;

namespace DemReview.Desktop.ViewModels;

/// <summary>Вкладка «Статистика»: ситуации раундов игрока, частотные таблицы «условия → действия», раунды матча.</summary>
public partial class StatisticsViewModel(
    ISituationAnalysisService situations,
    IRoundContextService contexts,
    ISettingsService settings) : ViewModelBase
{
    private MatchData? _match;
    private SituationAnalysis? _analysis;
    private int _version;

    [ObservableProperty]
    public partial FrequencyTable? SelectedTable { get; set; }

    [ObservableProperty]
    public partial string PlayerName { get; private set; } = "";

    public ObservableCollection<SituationRowViewModel> SituationRows { get; } = [];

    public ObservableCollection<FrequencyTable> Tables { get; } = [];

    public ObservableCollection<FrequencyRowViewModel> FrequencyRows { get; } = [];

    public ObservableCollection<RoundRowViewModel> MatchRounds { get; } = [];

    public bool HasPlayer => _analysis is not null;

    partial void OnSelectedTableChanged(FrequencyTable? value)
    {
        FrequencyRows.Clear();
        if (value is null)
            return;
        foreach (var row in value.Rows)
            FrequencyRows.Add(new FrequencyRowViewModel(row));
    }

    public void SetMatch(MatchData match)
    {
        _match = match;
        var context = contexts.Create(match, EconomyOptions());
        MatchRounds.Clear();
        foreach (var round in match.Rounds)
            MatchRounds.Add(new RoundRowViewModel(round, context.Economy(round.Number)));
        Clear();
    }

    public async Task ShowPlayerAsync(ulong? steamId)
    {
        var version = ++_version;
        if (_match is not { } match || steamId is not { } id)
        {
            Clear();
            return;
        }

        var s = settings.Current;
        var options = new SituationOptions(EconomyOptions(), s.AggressiveContactSeconds, s.PassiveContactSeconds);
        var analysis = await Task.Run(() => situations.Analyze(match, id, options));
        if (version != _version)
            return;

        _analysis = analysis;
        PlayerName = match.FindPlayer(id)?.Name ?? "";
        SituationRows.Clear();
        foreach (var round in analysis.Rounds)
            SituationRows.Add(new SituationRowViewModel(round));

        var previousTable = SelectedTable?.Name;
        Tables.Clear();
        foreach (var table in analysis.Tables)
            Tables.Add(table);
        SelectedTable = Tables.FirstOrDefault(t => t.Name == previousTable) ?? Tables.FirstOrDefault();
        OnPropertyChanged(nameof(HasPlayer));
    }

    private EconomyOptions EconomyOptions() =>
        new(settings.Current.BuyEcoMaxEquipment, settings.Current.BuyFullMinEquipment);

    private void Clear()
    {
        _analysis = null;
        PlayerName = "";
        SituationRows.Clear();
        Tables.Clear();
        SelectedTable = null;
        OnPropertyChanged(nameof(HasPlayer));
    }
}

public sealed class SituationRowViewModel(RoundSituation s)
{
    public int Round => s.Round;

    public string Side => Labels.Side(s.Side);

    public string Buy => $"{Labels.Buy(s.PlayerBuy)} / {Labels.Buy(s.TeamBuy)} / {Labels.Buy(s.EnemyBuy)}";

    public string Equipment => $"${s.PlayerEquipment} / ${s.TeamAverageEquipment}";

    public string Score => $"{s.ScoreUs}:{s.ScoreThem}";

    public string Route => s.RouteKey;

    public string FinalZone => s.FinalZone;

    public string Grenades => s.GrenadeCount == 0
        ? "—"
        : string.Join(", ", s.Grenades.OrderByDescending(g => g.Value).Select(g => $"{Texts.GrenadeName(g.Key)}×{g.Value}"));

    public string Contact => s.FirstContactTime is { } t ? $"{Texts.Time(t)} ({s.MatchupAtFirstContact})" : "—";

    public string Aggression => Labels.Aggression(s.Aggression);

    public string FirstDuel => Labels.FirstDuel(s.FirstDuel);

    public string Outcome => $"{(s.RoundWon ? "победа" : "поражение")}, {s.Kills} уб.{(s.Died ? ", погиб" : "")}";
}

public sealed class FrequencyRowViewModel(FrequencyRow row)
{
    public string Condition => row.Condition;

    public string Decision => row.Decision;

    public string Count => $"{row.Count} из {row.Total}";

    public string Share => $"{row.Share:P0}";

    /// <summary>Для сортировки по доле.</summary>
    public double ShareValue => row.Share;

    public string Rounds => string.Join(", ", row.Rounds);
}
