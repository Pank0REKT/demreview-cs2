using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemReview.Application.Context;
using DemReview.Application.Localization;
using DemReview.Application.Review;
using DemReview.Desktop.Services;
using DemReview.Domain;
using Microsoft.Extensions.Logging;

namespace DemReview.Desktop.ViewModels;

/// <summary>
/// Раздел «Разбор»: моменты, которые стоит пересмотреть (ошибки), клатчи, пост-планты и ретейки, дуэли.
/// У каждого момента — кнопка радара и копирование demo_gototick, чтобы посмотреть его в CS2.
/// </summary>
public partial class ReviewViewModel(
    IRoundContextService contexts,
    IClipboardService clipboard,
    ILogger<ReviewViewModel> logger) : ViewModelBase
{
    /// <summary>За сколько секунд до смерти открывать момент: видно, как игрок к ней пришёл.</summary>
    public const float MistakeLeadSeconds = 5;

    /// <summary>За сколько секунд до начала клатча или закладки бомбы открывать ситуацию.</summary>
    public const float SituationLeadSeconds = 2;

    private MatchData? _match;
    private IMatchContext? _context;
    private SelfReview? _review;
    private ulong? _steamId;
    private int _version;

    /// <summary>Сообщение для строки состояния: скопировано.</summary>
    public event Action<string>? Notice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlayer))]
    public partial string PlayerName { get; private set; } = "";

    public bool HasPlayer => PlayerName.Length > 0;

    [ObservableProperty]
    public partial bool IsBuilding { get; private set; }

    /// <summary>Демка разобрана прошлой версией: нет неброшенных гранат или тиков для demo_gototick.</summary>
    [ObservableProperty]
    public partial bool IsOldParse { get; private set; }

    public ObservableCollection<ReviewTile> Tiles { get; } = [];

    // ===== Ошибки =====

    public ObservableCollection<FilterOption> MistakeFilters { get; } = [];

    [ObservableProperty]
    public partial FilterOption? SelectedMistakeFilter { get; set; }

    partial void OnSelectedMistakeFilterChanged(FilterOption? value) => FillMistakes();

    public ObservableCollection<MistakeRowViewModel> MistakeRows { get; } = [];

    [ObservableProperty]
    public partial string MistakeSummary { get; private set; } = "";

    // ===== Клатчи, пост-планты, ретейки =====

    public ObservableCollection<FilterOption> SituationFilters { get; } = [];

    [ObservableProperty]
    public partial FilterOption? SelectedSituationFilter { get; set; }

    partial void OnSelectedSituationFilterChanged(FilterOption? value) => FillSituations();

    public ObservableCollection<SituationSummaryViewModel> SituationSummaries { get; } = [];

    public ObservableCollection<WinRateRow> PlaceRows { get; } = [];

    public ObservableCollection<KeySituationRowViewModel> SituationRows { get; } = [];

    [ObservableProperty]
    public partial string SituationHint { get; private set; } = "";

    // ===== Дуэли =====

    public ObservableCollection<WinRateRow> RangeRows { get; } = [];

    public ObservableCollection<WinRateRow> WeaponClassRows { get; } = [];

    public ObservableCollection<WinRateRow> StanceRows { get; } = [];

    public ObservableCollection<WinRateRow> WeaponRows { get; } = [];

    [ObservableProperty]
    public partial string DuelSummary { get; private set; } = "";

    public void SetMatch(MatchData match)
    {
        _version++;
        _match = match;
        _context = null;
        _review = null;
        Clear();
    }

    /// <summary>Разбор игрока считается в фоне.</summary>
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
            var context = _context;
            var review = await Task.Run(() =>
            {
                context ??= contexts.Create(match);
                return SelfReview.Analyze(context, id);
            });
            if (version != _version)
                return;

            _context = context;
            _review = review;
            Fill();
        }
        finally
        {
            if (version == _version)
                IsBuilding = false;
        }
    }

    public void RefreshLanguage()
    {
        if (_review is not null)
            Fill();
    }

    private void Clear()
    {
        PlayerName = "";
        IsOldParse = false;
        Tiles.Clear();
        MistakeFilters.Clear();
        MistakeRows.Clear();
        SituationFilters.Clear();
        SituationSummaries.Clear();
        PlaceRows.Clear();
        SituationRows.Clear();
        RangeRows.Clear();
        WeaponClassRows.Clear();
        StanceRows.Clear();
        WeaponRows.Clear();
        MistakeSummary = SituationHint = DuelSummary = "";
    }

    private void Fill()
    {
        if (_match is not { } match || _review is not { } review)
            return;

        PlayerName = match.FindPlayer(review.SteamId)?.Name ?? "";
        IsOldParse = !review.HasGrenadeData || !review.HasDemoTicks;
        FillTiles(review);
        FillMistakeFilters(review);
        FillSituationFilters(review);
        FillDuels(review);
    }

    private void FillTiles(SelfReview review)
    {
        Tiles.Clear();
        string OfDeaths(int count) => review.Deaths == 0 ? Loc.T("смертей нет", "no deaths")
            : Loc.T($"{Percent(count, review.Deaths)} смертей", $"{Percent(count, review.Deaths)} of deaths");

        var untraded = review.Count(MistakeKind.Untraded);
        Tiles.Add(new ReviewTile(Loc.T("Без размена", "Not traded"), $"{untraded} / {review.Deaths}", OfDeaths(untraded), false));

        var isolated = review.Count(MistakeKind.Isolated);
        Tiles.Add(new ReviewTile(Loc.T("В одиночку", "Alone"), $"{isolated}", OfDeaths(isolated), false));

        var grenades = review.Count(MistakeKind.UnusedGrenades);
        var lost = review.Mistakes.Sum(m => m.UnusedGrenadeValue);
        Tiles.Add(new ReviewTile(Loc.T("С гранатами", "With grenades"), review.HasGrenadeData ? $"{grenades}" : "—",
            review.HasGrenadeData ? Loc.T($"пропало ${lost}", $"${lost} lost") : Loc.T("нужен повторный разбор", "re-parse needed"),
            lost > 0));

        Tiles.Add(new ReviewTile(Loc.T("Повторный пик", "Re-peek"), $"{review.Count(MistakeKind.RePeek)}",
            Loc.T("погиб сразу после своего убийства", "died right after a kill"), false));

        var clutches = review.Of(KeySituationKind.Clutch).ToList();
        Tiles.Add(new ReviewTile(Loc.T("Клатчи", "Clutches"), $"{clutches.Count(c => c.Won)} / {clutches.Count}",
            clutches.Count == 0 ? Loc.T("не было", "none") : Loc.T($"выиграно {Percent(clutches.Count(c => c.Won), clutches.Count)}",
                $"{Percent(clutches.Count(c => c.Won), clutches.Count)} won"), false));

        var duels = review.Duels;
        Tiles.Add(new ReviewTile(Loc.T("Дуэли", "Duels"), $"{duels.Count(d => d.Won)}–{duels.Count(d => !d.Won)}",
            duels.Count == 0 ? Loc.T("не было", "none") : Loc.T($"выиграно {Percent(duels.Count(d => d.Won), duels.Count)}",
                $"{Percent(duels.Count(d => d.Won), duels.Count)} won"), false));
    }

    // ===== Ошибки =====

    private void FillMistakeFilters(SelfReview review)
    {
        var selected = SelectedMistakeFilter?.Value;
        MistakeFilters.Clear();
        MistakeFilters.Add(new FilterOption(() => Loc.T($"Все · {review.Mistakes.Count}", $"All · {review.Mistakes.Count}"), null));
        foreach (var kind in Enum.GetValues<MistakeKind>())
        {
            var count = review.Count(kind);
            MistakeFilters.Add(new FilterOption(() => $"{ReviewTexts.Mistake(kind)} · {count}", kind));
        }
        SelectedMistakeFilter = MistakeFilters.FirstOrDefault(f => Equals(f.Value, selected)) ?? MistakeFilters[0];
        FillMistakes();
    }

    private void FillMistakes()
    {
        MistakeRows.Clear();
        if (_match is not { } match || _review is not { } review)
            return;

        var kind = SelectedMistakeFilter?.Value as MistakeKind?;
        foreach (var death in review.Mistakes.Where(m => kind is null || m.Has(kind.Value)))
            MistakeRows.Add(new MistakeRowViewModel(death, match));

        MistakeSummary = review.Deaths == 0
            ? Loc.T("Игрок ни разу не погиб.", "The player never died.")
            : Loc.T($"Смертей: {review.Deaths}, из них со спорными моментами: {review.Mistakes.Count}.",
                $"Deaths: {review.Deaths}, with questionable moments: {review.Mistakes.Count}.");
    }

    // ===== Клатчи, пост-планты, ретейки =====

    private void FillSituationFilters(SelfReview review)
    {
        SituationSummaries.Clear();
        foreach (var kind in Enum.GetValues<KeySituationKind>())
            SituationSummaries.Add(new SituationSummaryViewModel(kind, review.Of(kind).ToList()));

        var selected = SelectedSituationFilter?.Value;
        SituationFilters.Clear();
        foreach (var kind in Enum.GetValues<KeySituationKind>())
        {
            var count = review.Of(kind).Count();
            SituationFilters.Add(new FilterOption(() => $"{ReviewTexts.Situation(kind)} · {count}", kind));
        }
        SelectedSituationFilter = SituationFilters.FirstOrDefault(f => Equals(f.Value, selected)) ?? SituationFilters[0];
        FillSituations();
    }

    private void FillSituations()
    {
        PlaceRows.Clear();
        SituationRows.Clear();
        if (_match is not { } match || _review is not { } review || SelectedSituationFilter?.Value is not KeySituationKind kind)
            return;

        var situations = review.Of(kind).ToList();
        foreach (var place in KeySituationAnalysis.Places(situations))
            PlaceRows.Add(new WinRateRow(Texts.ZoneOrUnknown(place.Place), place.Wins, place.Rounds - place.Wins));
        foreach (var situation in situations)
            SituationRows.Add(new KeySituationRowViewModel(situation, match));

        SituationHint = kind switch
        {
            KeySituationKind.Clutch => Loc.T("Клатч начинается, когда погиб последний союзник. Позиция — зона, где игрок провёл клатч дольше всего.",
                "A clutch starts when the last teammate dies. The position is the zone where the player spent most of the clutch."),
            KeySituationKind.PostPlant => Loc.T("Бомба заложена, игрок за T жив. Позиция — зона, где он ждал после закладки дольше всего.",
                "The bomb is planted and the player is alive on T. The position is the zone where they waited longest after the plant."),
            _ => Loc.T("Бомба заложена, игрок за CT жив. Позиция — зона, где он провёл ретейк дольше всего.",
                "The bomb is planted and the player is alive on CT. The position is the zone where they spent most of the retake."),
        };
    }

    // ===== Дуэли =====

    private void FillDuels(SelfReview review)
    {
        var duels = review.Duels;
        Fill(RangeRows, DuelAnalysis.ByRange(duels).Select(r => new WinRateRow(ReviewTexts.Range(r.Key), r.Wins, r.Losses)));
        Fill(WeaponClassRows, DuelAnalysis.ByWeaponClass(duels).Select(r => new WinRateRow(ReviewTexts.WeaponClassName(r.Key), r.Wins, r.Losses)));
        Fill(StanceRows, DuelAnalysis.ByStance(duels).Select(r => new WinRateRow(ReviewTexts.Stance(r.Key), r.Wins, r.Losses)));
        Fill(WeaponRows, DuelAnalysis.ByWeapon(duels).Take(8).Select(r => new WinRateRow(Texts.Weapon(r.Key.Length > 0 ? r.Key : "?"), r.Wins, r.Losses)));

        var headshots = duels.Count(d => d.Won && d.Headshot);
        var wins = duels.Count(d => d.Won);
        DuelSummary = duels.Count == 0
            ? Loc.T("Дуэлей нет.", "No duels.")
            : Loc.T($"Дуэлей: {duels.Count}, выиграно {wins} ({Percent(wins, duels.Count)}), в голову — {headshots} из {wins} побед.",
                $"Duels: {duels.Count}, won {wins} ({Percent(wins, duels.Count)}), headshots in {headshots} of {wins} wins.");
    }

    private static void Fill(ObservableCollection<WinRateRow> target, IEnumerable<WinRateRow> rows)
    {
        target.Clear();
        foreach (var row in rows)
            target.Add(row);
    }

    // ===== Команды =====

    /// <summary>Скопировать demo_gototick для момента.</summary>
    [RelayCommand]
    private async Task CopyGoToTickAsync(ReviewMoment? moment)
    {
        if (moment is null || _match is not { } match)
            return;

        if (DemoCommands.GoToTick(match, moment.Round, moment.Tick, moment.LeadSeconds) is not { } command)
        {
            Notice?.Invoke(Loc.T("Для этой демки нет тиков: разберите её заново (Shift+F5).", "No demo ticks for this demo: re-parse it (Shift+F5)."));
            return;
        }

        var demo = match.PartOf(moment.Round)?.FileName ?? Path.GetFileName(match.SourcePath);
        try
        {
            var copied = await clipboard.SetTextAsync(command);
            Notice?.Invoke(copied
                ? Loc.T($"Скопировано: {command} — откройте {demo} в CS2 и вставьте команду в консоль.",
                    $"Copied: {command} — open {demo} in CS2 and paste it into the console.")
                : Loc.T("Буфер обмена недоступен.", "The clipboard is not available."));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Не удалось скопировать в буфер обмена");
            Notice?.Invoke(Loc.T($"Не удалось скопировать: {ex.Message}", $"Could not copy: {ex.Message}"));
        }
    }

    private static string Percent(int part, int total) =>
        total == 0 ? "—" : (part / (double)total).ToString("P0", Loc.Culture);
}

/// <summary>Момент демки для радара и demo_gototick: раунд, тик и сколько секунд показать до него.</summary>
public sealed record ReviewMoment(int Round, int Tick, float LeadSeconds);

/// <summary>Плитка разбора: подпись, число и пояснение. <paramref name="IsWarning"/> — выделить красным.</summary>
public sealed record ReviewTile(string Label, string Value, string Detail, bool IsWarning);

/// <summary>Строка «победы — поражения»: дистанция, оружие, позиция.</summary>
public sealed class WinRateRow(string label, int wins, int losses)
{
    public string Label => label;

    public int Total => wins + losses;

    public string RecordText => $"{wins}–{losses}";

    public double WinRate => Total == 0 ? 0 : wins / (double)Total;

    public string WinRateText => WinRate.ToString("P0", Loc.Culture);

    /// <summary>Выигрывает чаще, чем проигрывает (и не на единичной выборке).</summary>
    public bool IsGood => Total >= 2 && WinRate > 0.5;

    public bool IsBad => Total >= 2 && WinRate < 0.5;
}

/// <summary>Смерть игрока со спорными моментами.</summary>
public sealed class MistakeRowViewModel(DeathReview death, MatchData match)
{
    public DeathReview Death => death;

    public ReviewMoment Moment { get; } = new(death.Round, death.Tick, ReviewViewModel.MistakeLeadSeconds);

    public bool CanGoToTick => match.FindRound(death.Round)?.DemoTickOffset is not null;

    public bool IsT => death.Side == Side.T;

    public string SideText => Texts.SideShort(death.Side);

    public string Header
    {
        get
        {
            var header = $"{Loc.T("Раунд", "Round")} {death.Round} · {Texts.Time(death.Time)} · {Texts.ZoneOrUnknown(death.Place)}";
            return match.PartOf(death.Round) is { } part ? $"{header} · {part.FileName}" : header;
        }
    }

    public string KilledByText
    {
        get
        {
            var killer = death.KillerSteamId is { } id && id != death.Death.VictimSteamId ? match.FindPlayer(id)?.Name : null;
            var weapon = Texts.Weapon(death.Death.Weapon);
            return killer is null
                ? Loc.T($"Погиб: {weapon}", $"Died: {weapon}")
                : Loc.T($"Убийца: {killer} · {weapon}{(death.Death.Headshot ? " · в голову" : "")}",
                    $"Killed by {killer} · {weapon}{(death.Death.Headshot ? " · headshot" : "")}");
        }
    }

    public IReadOnlyList<string> Tags => death.Mistakes.Select(ReviewTexts.Mistake).ToList();

    /// <summary>Подробности по каждой ошибке, через точку.</summary>
    public string DetailText
    {
        get
        {
            var parts = new List<string>();
            if (death.Has(MistakeKind.Untraded))
                parts.Add(Loc.T($"живых союзников: {death.AliveAllies}, никто не убил убийцу", $"teammates alive: {death.AliveAllies}, none killed the killer"));
            if (death.Has(MistakeKind.Isolated) && death.TeammateDistance is { } distance)
                parts.Add(Loc.T($"ближайший союзник в {distance:F0} units (~{distance * 0.019:F0} м)",
                    $"nearest teammate {distance:F0} units away (~{distance * 0.019:F0} m)"));
            if (death.Has(MistakeKind.UnusedGrenades) && death.UnusedGrenades is { } grenades)
                parts.Add(Loc.T($"не бросил: {ReviewTexts.Grenades(grenades)} — ${death.UnusedGrenadeValue}",
                    $"unused: {ReviewTexts.Grenades(grenades)} — ${death.UnusedGrenadeValue}"));
            if (death.Has(MistakeKind.RePeek) && death.RePeekAfter is { } after)
            {
                var victim = death.RePeekVictim is { } v ? match.FindPlayer(v)?.Name ?? "?" : "?";
                var seconds = after.ToString("F1", CultureInfo.CurrentCulture);
                parts.Add(Loc.T($"через {seconds} с после убийства {victim} на том же месте",
                    $"{seconds} s after killing {victim}, on the same spot"));
            }
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>Сводка по виду ситуации: сколько раз и сколько выиграно.</summary>
public sealed class SituationSummaryViewModel(KeySituationKind kind, IReadOnlyList<KeySituation> situations)
{
    public string Title => ReviewTexts.Situation(kind);

    public string Value => $"{situations.Count(s => s.Won)} / {situations.Count}";

    public string Detail
    {
        get
        {
            if (situations.Count == 0)
                return Loc.T("не было", "none");
            var won = situations.Count(s => s.Won) / (double)situations.Count;
            var text = Loc.T($"выиграно {won.ToString("P0", Loc.Culture)}", $"{won.ToString("P0", Loc.Culture)} won");
            if (kind != KeySituationKind.Clutch)
                return text;

            // Клатчи по соотношению: 1v1, 1v2…
            var matchups = situations.GroupBy(s => s.Enemies).OrderBy(g => g.Key)
                .Select(g => $"1v{g.Key}: {g.Count(s => s.Won)}/{g.Count()}");
            return $"{text} · {string.Join(", ", matchups)}";
        }
    }
}

/// <summary>Раунд с ключевой ситуацией.</summary>
public sealed class KeySituationRowViewModel(KeySituation situation, MatchData match)
{
    public ReviewMoment Moment { get; } = new(situation.Round, situation.Tick, ReviewViewModel.SituationLeadSeconds);

    public bool CanGoToTick => match.FindRound(situation.Round)?.DemoTickOffset is not null;

    public bool Won => situation.Won;

    public bool IsT => situation.Side == Side.T;

    public string SideText => Texts.SideShort(situation.Side);

    public string Header
    {
        get
        {
            var matchup = $"{situation.Allies + 1}v{situation.Enemies}";
            var site = situation.Kind != KeySituationKind.Clutch && situation.Site != BombSite.Unknown ? $" · {situation.Site}" : "";
            var header = $"{Loc.T("Раунд", "Round")} {situation.Round} · {Texts.Time(situation.Time)} · {matchup}{site}";
            return match.PartOf(situation.Round) is { } part ? $"{header} · {part.FileName}" : header;
        }
    }

    public string DetailText
    {
        get
        {
            var parts = new List<string> { Texts.ZoneOrUnknown(situation.Place) };
            parts.Add(situation.Survived ? Loc.T("выжил", "survived") : Loc.T("погиб", "died"));
            if (situation.Kills > 0)
                parts.Add(Loc.T($"убийств: {situation.Kills}", $"kills: {situation.Kills}"));
            return string.Join(" · ", parts);
        }
    }

    public string OutcomeText => situation.Won ? Loc.T("раунд выигран", "round won") : Loc.T("раунд проигран", "round lost");
}

public static class ReviewTexts
{
    public static string Mistake(MistakeKind kind) => kind switch
    {
        MistakeKind.Untraded => Loc.T("Без размена", "Not traded"),
        MistakeKind.Isolated => Loc.T("В одиночку", "Alone"),
        MistakeKind.UnusedGrenades => Loc.T("С гранатами", "Unused grenades"),
        _ => Loc.T("Повторный пик", "Re-peek"),
    };

    public static string Situation(KeySituationKind kind) => kind switch
    {
        KeySituationKind.Clutch => Loc.T("Клатчи", "Clutches"),
        KeySituationKind.PostPlant => Loc.T("Пост-плант", "Post-plant"),
        _ => Loc.T("Ретейк", "Retake"),
    };

    public static string Range(DuelRange range) => range switch
    {
        DuelRange.Close => Loc.T("Близко · до 10 м", "Close · under 10 m"),
        DuelRange.Medium => Loc.T("Средняя · 10–23 м", "Medium · 10–23 m"),
        _ => Loc.T("Далеко · от 23 м", "Long · over 23 m"),
    };

    public static string WeaponClassName(WeaponClass weaponClass) => weaponClass switch
    {
        WeaponClass.Pistol => Loc.T("Пистолеты", "Pistols"),
        WeaponClass.Smg => Loc.T("ПП", "SMGs"),
        WeaponClass.Rifle => Loc.T("Винтовки", "Rifles"),
        WeaponClass.Sniper => Loc.T("Снайперские", "Snipers"),
        WeaponClass.Heavy => Loc.T("Дробовики и пулемёты", "Shotguns and MGs"),
        _ => Loc.T("Другое", "Other"),
    };

    public static string Stance(DuelStance stance) => stance switch
    {
        DuelStance.Peeking => Loc.T("Выходил на угол", "Peeking"),
        _ => Loc.T("Держал угол", "Holding"),
    };

    /// <summary>«дым, флешка ×2».</summary>
    public static string Grenades(IEnumerable<GrenadeType> grenades) =>
        string.Join(", ", grenades.GroupBy(g => g).Select(g => g.Count() > 1 ? $"{Texts.GrenadeName(g.Key)} ×{g.Count()}" : Texts.GrenadeName(g.Key)));
}
