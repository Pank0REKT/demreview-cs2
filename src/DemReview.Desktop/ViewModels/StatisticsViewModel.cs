using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DemReview.Application;
using DemReview.Application.Context;
using DemReview.Application.Filters;
using DemReview.Application.Localization;
using DemReview.Application.Patterns;
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
    private IMatchContext? _context;
    private ulong? _steamId;
    private AnalysisFilter _filter = AnalysisFilter.None;

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
        _context = context;
        _filter = AnalysisFilter.None;
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
        _steamId = id;
        PlayerName = match.FindPlayer(id)?.Name ?? "";
        ApplyFilter();
        OnPropertyChanged(nameof(HasPlayer));
    }

    /// <summary>Фильтр (раунд, сторона, закупка) применяется к ситуациям и пересчитывает частотные таблицы.</summary>
    public void SetFilter(AnalysisFilter filter)
    {
        _filter = filter;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        if (_analysis is not { } analysis || _context is null || _steamId is not { } id)
            return;

        var rounds = analysis.Rounds.Where(r => _filter.IncludesRound(_context, id, r.Round)).ToList();
        SituationRows.Clear();
        foreach (var round in rounds)
            SituationRows.Add(new SituationRowViewModel(round));
        FillTiles(rounds);

        var previousTable = SelectedTable?.Name;
        Tables.Clear();
        foreach (var table in situations.BuildTables(rounds))
            Tables.Add(table);
        SelectedTable = Tables.FirstOrDefault(t => t.Name == previousTable) ?? Tables.FirstOrDefault();
    }

    /// <summary>Плитки над таблицей: раунды, победы, урон, первый контакт — всего и по сторонам.</summary>
    public ObservableCollection<StatTile> Tiles { get; } = [];

    private void FillTiles(IReadOnlyList<RoundSituation> rounds)
    {
        var t = rounds.Where(r => r.Side == Side.T).ToList();
        var ct = rounds.Where(r => r.Side == Side.CT).ToList();

        static string WinShare(List<RoundSituation> side) => side.Count == 0 ? "—" : $"{(double)side.Count(r => r.RoundWon) / side.Count:P0}";
        static string Damage(IReadOnlyCollection<RoundSituation> side) => side.Count == 0 ? "—" : $"{side.Average(r => r.Damage):F0}";
        static string Contact(IEnumerable<RoundSituation> side)
        {
            var times = side.Where(r => r.FirstContactTime is not null).Select(r => r.FirstContactTime!.Value).ToList();
            return times.Count == 0 ? "—" : Texts.Time(PatternStats.Median(times));
        }

        Tiles.Clear();
        Tiles.Add(new StatTile(Loc.T("Раундов", "Rounds"), $"{rounds.Count}", $"T {t.Count}", $"CT {ct.Count}"));
        Tiles.Add(new StatTile(Loc.T("Побед команды", "Team wins"), $"{rounds.Count(r => r.RoundWon)}", $"T {WinShare(t)}", $"CT {WinShare(ct)}"));
        Tiles.Add(new StatTile(Loc.T("Урон за раунд", "Damage per round"), Damage(rounds), $"T {Damage(t)}", $"CT {Damage(ct)}"));
        Tiles.Add(new StatTile(Loc.T("Первый контакт", "First contact"), Contact(rounds), $"T {Contact(t)}", $"CT {Contact(ct)}"));
    }

    private EconomyOptions EconomyOptions() =>
        new(settings.Current.BuyEcoMaxEquipment, settings.Current.BuyFullMinEquipment);

    private void Clear()
    {
        _analysis = null;
        PlayerName = "";
        SituationRows.Clear();
        Tiles.Clear();
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

    /// <summary>Шкала времени первого контакта в таблице — до полутора минут.</summary>
    private const float ContactScaleSeconds = 90;

    public bool IsT => s.Side == Domain.Side.T;

    public IReadOnlyList<BuyChip> Buys { get; } = [new(s.PlayerBuy), new(s.TeamBuy), new(s.EnemyBuy)];

    public string OwnEquipment => $"${s.PlayerEquipment}";

    public string TeamEquipment => $"/ ${s.TeamAverageEquipment}";

    public string ScoreText => $"{s.ScoreUs} : {s.ScoreThem}";

    public IReadOnlyList<GrenadeChip> GrenadeChips { get; } = s.Grenades.Where(g => g.Value > 0).OrderByDescending(g => g.Value)
        .Select(g => new GrenadeChip(g.Key, g.Value)).ToList();

    public bool HasNoGrenades => s.GrenadeCount == 0;

    public bool HasContact => s.FirstContactTime is not null;

    public string ContactTime => s.FirstContactTime is { } t ? Texts.Time(t) : "—";

    public string Matchup => s.FirstContactTime is null ? "" : s.MatchupAtFirstContact.Replace("v", " v ", StringComparison.Ordinal);

    /// <summary>Заполнение шкалы контакта, 0..1.</summary>
    public double ContactFraction => s.FirstContactTime is { } t ? Math.Clamp(t / ContactScaleSeconds, 0.04, 1) : 0;

    public bool IsAggressive => s.Aggression == global::DemReview.Application.Situations.Aggression.Aggressive;

    public bool IsPassive => s.Aggression == global::DemReview.Application.Situations.Aggression.Passive;

    public bool RoundWon => s.RoundWon;

    public string Aggression => Labels.Aggression(s.Aggression);

    public string FirstDuel => Labels.FirstDuel(s.FirstDuel);

    public string Outcome => Loc.T($"{(s.RoundWon ? "победа" : "поражение")}, {s.Kills} уб.{(s.Died ? ", погиб" : "")}",
        $"{(s.RoundWon ? "win" : "loss")}, {s.Kills} kills{(s.Died ? ", died" : "")}");

    public int Damage => s.Damage;
}

/// <summary>Плитка KPI: подпись, общее значение и значения по сторонам.</summary>
public sealed record StatTile(string Label, string Value, string TText, string CtText);

/// <summary>Бейдж типа закупки: короткая подпись и цвет по типу.</summary>
public sealed class BuyChip(BuyType buy)
{
    public string Label => buy switch
    {
        BuyType.Pistol => Loc.T("пист.", "pistol"),
        BuyType.Eco => Loc.T("эко", "eco"),
        BuyType.Force => Loc.T("форс", "force"),
        _ => Loc.T("фулл", "full"),
    };

    public string Tooltip => Labels.Buy(buy);

    public bool IsPistol => buy == BuyType.Pistol;

    public bool IsEco => buy == BuyType.Eco;

    public bool IsForce => buy == BuyType.Force;

    public bool IsFull => buy == BuyType.FullBuy;
}

/// <summary>Граната в строке ситуации: цветная точка, название и количество.</summary>
public sealed class GrenadeChip(GrenadeType type, int count)
{
    public string Label => type switch
    {
        GrenadeType.Incendiary => Loc.T("Зажиг.", "Incend."),
        _ => Texts.GrenadeName(type),
    };

    public string CountText => $"×{count}";

    public bool HasCount => count > 1;

    public Avalonia.Media.IBrush Brush { get; } = new Avalonia.Media.SolidColorBrush(type switch
    {
        GrenadeType.HighExplosive => Avalonia.Media.Color.Parse("#F28B85"),
        GrenadeType.Flashbang => Avalonia.Media.Color.Parse("#F2E9A0"),
        GrenadeType.Smoke => Avalonia.Media.Color.Parse("#B4BCC9"),
        GrenadeType.Molotov or GrenadeType.Incendiary => Avalonia.Media.Color.Parse("#F0874A"),
        _ => Avalonia.Media.Color.Parse("#8FD18F"),
    });
}

public sealed class FrequencyRowViewModel(FrequencyRow row)
{
    public string Condition => row.Condition;

    public string Decision => row.Decision;

    public string Count => Loc.T($"{row.Count} из {row.Total}", $"{row.Count} of {row.Total}");

    public string Share => $"{row.Share:P0}";

    /// <summary>Для сортировки по доле.</summary>
    public double ShareValue => row.Share;

    public string Rounds => string.Join(", ", row.Rounds);
}
