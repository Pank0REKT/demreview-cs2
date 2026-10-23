using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemReview.Application.Filters;
using DemReview.Application.Grenades;
using DemReview.Application.Lineups;
using DemReview.Application.Localization;
using DemReview.Application.Maps;
using DemReview.Desktop.Services;
using DemReview.Domain;
using Microsoft.Extensions.Logging;

namespace DemReview.Desktop.ViewModels;

/// <summary>
/// Вкладка «Гранаты»: эффективность гранат выбранного игрока и всех игроков матча (урон HE и огня, ослепления
/// противников и союзников, убийства ослеплённых) и библиотека раскидок с командами setpos / setang.
/// </summary>
public partial class GrenadesViewModel(
    ILineupLibrary lineups,
    IClipboardService clipboard,
    ILogger<GrenadesViewModel> logger) : ViewModelBase
{
    private MatchData? _match;
    private GrenadeEffectiveness _effectiveness = GrenadeEffectiveness.Empty;
    private IReadOnlyDictionary<int, string> _landZones = new Dictionary<int, string>();
    private ulong? _steamId;
    private int _matchVersion;

    /// <summary>Сообщение для строки состояния: скопировано, сохранено.</summary>
    public event Action<string>? Notice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlayer))]
    public partial string PlayerName { get; private set; } = "";

    public bool HasPlayer => PlayerName.Length > 0;

    [ObservableProperty]
    public partial bool HasPlayerThrows { get; private set; }

    /// <summary>Плитки эффективности выбранного игрока.</summary>
    public ObservableCollection<GrenadeTile> Tiles { get; } = [];

    /// <summary>Все игроки матча, бросавшие гранаты.</summary>
    public ObservableCollection<PlayerGrenadeRow> PlayerRows { get; } = [];

    /// <summary>Броски выбранного игрока с результатом.</summary>
    public ObservableCollection<GrenadeOutcomeRow> ThrowRows { get; } = [];

    public IReadOnlyList<FilterOption> TypeFilters { get; } =
    [
        new("Все", "All", null),
        new(() => "HE", GrenadeFilterGroup.HighExplosive),
        new("Флешки", "Flashes", GrenadeFilterGroup.Flash),
        new("Дым", "Smokes", GrenadeFilterGroup.Smoke),
        new("Огонь", "Fire", GrenadeFilterGroup.Fire),
        new("Ложные", "Decoys", GrenadeFilterGroup.Decoy),
    ];

    [ObservableProperty]
    public partial FilterOption? SelectedTypeFilter { get; set; }

    partial void OnSelectedTypeFilterChanged(FilterOption? value) => FillThrows();

    // ===== Библиотека раскидок =====

    private IReadOnlyList<Lineup> _allLineups = [];

    public ObservableCollection<LineupRowViewModel> Lineups { get; } = [];

    /// <summary>Карты библиотеки раскидок: «Все карты» и карты с числом раскидок.</summary>
    public ObservableCollection<FilterOption> LineupMaps { get; } = [];

    [ObservableProperty]
    public partial FilterOption? SelectedLineupMap { get; set; }

    partial void OnSelectedLineupMapChanged(FilterOption? value) => FillLineups();

    [ObservableProperty]
    public partial FilterOption? SelectedLineupType { get; set; }

    partial void OnSelectedLineupTypeChanged(FilterOption? value) => FillLineups();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoLineups))]
    public partial bool HasLineups { get; private set; }

    public bool HasNoLineups => !HasLineups;

    [ObservableProperty]
    public partial string LineupSummary { get; private set; } = "";

    public string LibraryPath => lineups.FilePath;

    /// <summary>Загрузить библиотеку раскидок при запуске.</summary>
    public async Task InitializeAsync()
    {
        SelectedTypeFilter = TypeFilters[0];
        SelectedLineupType = TypeFilters[0];
        await ReloadLineupsAsync();
    }

    /// <summary>Новая демка или сводный матч: эффективность гранат всех игроков считается в фоне.</summary>
    public async Task SetMatchAsync(MatchData match)
    {
        var version = ++_matchVersion;
        _match = match;
        _effectiveness = GrenadeEffectiveness.Empty;
        _landZones = new Dictionary<int, string>();
        PlayerRows.Clear();
        ThrowRows.Clear();
        Tiles.Clear();
        FillLineupMaps(preferMap: match.MapName);

        var (effectiveness, landZones) = await Task.Run(() =>
        {
            var zones = new ZoneLocator(match);
            var land = match.Grenades
                .Where(g => g.DetonatePosition.HasValue)
                .ToDictionary(g => g.Id, g => zones.Locate(g.DetonatePosition!.Value));
            return (GrenadeEffectivenessAnalysis.Analyze(match), (IReadOnlyDictionary<int, string>)land);
        });
        if (version != _matchVersion)
            return;

        _effectiveness = effectiveness;
        _landZones = landZones;
        FillPlayers();
        FillPlayer();
    }

    public void ShowPlayer(ulong? steamId)
    {
        _steamId = steamId;
        FillPlayers();
        FillPlayer();
    }

    public void RefreshLanguage()
    {
        FillPlayers();
        FillPlayer();
        FillLineupMaps(preferMap: null);
    }

    private void FillPlayers()
    {
        PlayerRows.Clear();
        if (_match is not { } match)
            return;
        foreach (var stats in _effectiveness.Players
                     .OrderByDescending(p => p.HeDamage + p.FireDamage)
                     .ThenByDescending(p => p.EnemiesBlinded))
        {
            if (match.FindPlayer(stats.SteamId) is { } player)
                PlayerRows.Add(new PlayerGrenadeRow(player, stats, stats.SteamId == _steamId));
        }
    }

    private void FillPlayer()
    {
        Tiles.Clear();
        if (_match is not { } match || _steamId is not { } id)
        {
            PlayerName = "";
            FillThrows();
            return;
        }

        PlayerName = match.FindPlayer(id)?.Name ?? "";
        var s = _effectiveness.Of(id);
        if (s is not null)
        {
            Tiles.Add(new GrenadeTile(Loc.T("Урон HE", "HE damage"), $"{s.HeDamage}",
                s.HeThrown == 0 ? Loc.T("HE не бросал", "no HEs thrown")
                    : Loc.T($"{s.HeThrown} шт. · {s.HeDamagePerThrow:F0} за гранату", $"{s.HeThrown} thrown · {s.HeDamagePerThrow:F0} per grenade"),
                false));
            Tiles.Add(new GrenadeTile(Loc.T("Урон огнём", "Fire damage"), $"{s.FireDamage}",
                s.FireThrown == 0 ? Loc.T("молотовы не бросал", "no molotovs thrown")
                    : Loc.T($"{s.FireThrown} шт. · {s.FireDamagePerThrow:F0} за гранату", $"{s.FireThrown} thrown · {s.FireDamagePerThrow:F0} per grenade"),
                false));
            Tiles.Add(new GrenadeTile(Loc.T("Ослепил противников", "Enemies blinded"), $"{s.EnemiesBlinded}",
                s.FlashesThrown == 0 ? Loc.T("флешки не бросал", "no flashes thrown")
                    : Loc.T($"{s.FlashesThrown} флеш. · {s.EnemiesPerFlash:F1} за флешку · {s.EnemyBlindSeconds:F1} с",
                        $"{s.FlashesThrown} flashes · {s.EnemiesPerFlash:F1} per flash · {s.EnemyBlindSeconds:F1} s"),
                false));
            Tiles.Add(new GrenadeTile(Loc.T("Убийства", "Kills"), $"{s.FlashKills + s.GrenadeKills}",
                Loc.T($"по ослеплённым: {s.FlashKills} · гранатой: {s.GrenadeKills}", $"on flashed enemies: {s.FlashKills} · by grenade: {s.GrenadeKills}"),
                false));
            Tiles.Add(new GrenadeTile(Loc.T("Ослепил своих", "Teammates blinded"), $"{s.TeammatesBlinded}",
                Loc.T($"{s.TeammateBlindSeconds:F1} с · урон своим гранатами: {s.TeamDamage}",
                    $"{s.TeammateBlindSeconds:F1} s · grenade damage to teammates: {s.TeamDamage}"),
                s.TeammatesBlinded > 0));
        }

        FillThrows();
    }

    private void FillThrows()
    {
        ThrowRows.Clear();
        if (_match is { } match && _steamId is { } id)
        {
            var group = SelectedTypeFilter?.Value as GrenadeFilterGroup?;
            foreach (var g in match.GrenadesOf(id).Where(g => group is null || AnalysisFilter.GroupOf(g.Type) == group).OrderBy(g => g.ThrowTick))
            {
                if (_effectiveness.Outcomes.TryGetValue(g.Id, out var outcome))
                    ThrowRows.Add(new GrenadeOutcomeRow(outcome, _landZones.GetValueOrDefault(g.Id, ""), match));
            }
        }
        HasPlayerThrows = ThrowRows.Count > 0;
    }

    // ===== Команды бросков (и с радара) =====

    /// <summary>Скопировать setpos / setang броска.</summary>
    [RelayCommand]
    private async Task CopyThrowAsync(GrenadeThrow? grenade)
    {
        if (grenade is null)
            return;
        await CopyAsync(LineupCommands.SetPos(grenade.ThrowerPosition, grenade.ThrowerPitch, grenade.ThrowerYaw));
    }

    /// <summary>Сохранить бросок в библиотеку раскидок.</summary>
    [RelayCommand]
    private async Task SaveThrowAsync(GrenadeThrow? grenade)
    {
        if (grenade is null || _match is not { } match)
            return;

        var lineup = LineupFactory.FromThrow(match, grenade, _landZones.GetValueOrDefault(grenade.Id, ""), DateTime.UtcNow);
        try
        {
            var (saved, added) = await Task.Run(() => lineups.Add(lineup));
            Notice?.Invoke(added
                ? Loc.T($"Раскидка сохранена: {saved.Title} ({saved.MapName}). Она в разделе «Гранаты».",
                    $"Lineup saved: {saved.Title} ({saved.MapName}). Find it in the Grenades section.")
                : Loc.T($"Эта раскидка уже есть в библиотеке: {saved.Title}.", $"This lineup is already in the library: {saved.Title}."));
            if (added)
                await ReloadLineupsAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Не удалось сохранить раскидку");
            Notice?.Invoke(Loc.T($"Не удалось сохранить раскидку: {ex.Message}", $"Could not save the lineup: {ex.Message}"));
        }
    }

    [RelayCommand]
    private Task CopyLineupAsync(LineupRowViewModel? row) =>
        row is null ? Task.CompletedTask : CopyAsync(LineupCommands.SetPos(row.Lineup));

    [RelayCommand]
    private async Task RemoveLineupAsync(LineupRowViewModel? row)
    {
        if (row is null)
            return;
        await Task.Run(() => lineups.Remove(row.Lineup.Id));
        Notice?.Invoke(Loc.T($"Раскидка удалена: {row.Title}", $"Lineup removed: {row.Title}"));
        await ReloadLineupsAsync();
    }

    private async Task CopyAsync(string command)
    {
        try
        {
            var copied = await clipboard.SetTextAsync(command);
            Notice?.Invoke(copied
                ? Loc.T($"Скопировано: {command} — вставьте в консоль CS2 на своём сервере (нужен sv_cheats 1).",
                    $"Copied: {command} — paste it into the CS2 console on your own server (needs sv_cheats 1).")
                : Loc.T("Буфер обмена недоступен.", "The clipboard is not available."));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Не удалось скопировать в буфер обмена");
            Notice?.Invoke(Loc.T($"Не удалось скопировать: {ex.Message}", $"Could not copy: {ex.Message}"));
        }
    }

    private async Task ReloadLineupsAsync()
    {
        _allLineups = await Task.Run(lineups.List);
        FillLineupMaps(preferMap: null);
    }

    /// <param name="preferMap">Карта, которую выбрать, если на ней есть раскидки (карта открытой демки).</param>
    private void FillLineupMaps(string? preferMap)
    {
        var selected = SelectedLineupMap?.Value as string;
        if (preferMap is not null && _allLineups.Any(l => l.MapName == preferMap))
            selected = preferMap;

        LineupMaps.Clear();
        LineupMaps.Add(new FilterOption(() => Loc.T($"Все карты · {_allLineups.Count}", $"All maps · {_allLineups.Count}"), null));
        foreach (var map in _allLineups.GroupBy(l => l.MapName).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal))
        {
            var count = map.Count();
            LineupMaps.Add(new FilterOption(() => $"{map.Key} · {count}", map.Key));
        }

        SelectedLineupMap = LineupMaps.FirstOrDefault(m => Equals(m.Value, selected)) ?? LineupMaps[0];
        FillLineups();
    }

    private void FillLineups()
    {
        var map = SelectedLineupMap?.Value as string;
        var group = SelectedLineupType?.Value as GrenadeFilterGroup?;
        Lineups.Clear();
        foreach (var lineup in _allLineups.Where(l => (map is null || l.MapName == map) && (group is null || AnalysisFilter.GroupOf(l.Type) == group)))
            Lineups.Add(new LineupRowViewModel(lineup, Rename));
        HasLineups = _allLineups.Count > 0;
        LineupSummary = _allLineups.Count == 0
            ? Loc.T("Раскидок пока нет.", "No lineups yet.")
            : Loc.T($"Показано {Lineups.Count} из {_allLineups.Count}", $"Showing {Lineups.Count} of {_allLineups.Count}");
    }

    private void Rename(LineupRowViewModel row, string title)
    {
        _ = Task.Run(() => lineups.Rename(row.Lineup.Id, title));
        _allLineups = _allLineups.Select(l => l.Id == row.Lineup.Id ? l with { Title = title } : l).ToList();
    }
}

/// <summary>Плитка эффективности: подпись, число и пояснение. <paramref name="IsWarning"/> — граната задела своих.</summary>
public sealed record GrenadeTile(string Label, string Value, string Detail, bool IsWarning);

/// <summary>Строка таблицы эффективности гранат всех игроков матча.</summary>
public sealed class PlayerGrenadeRow(PlayerInfo player, PlayerGrenadeStats stats, bool isSelected)
{
    public string Name => player.Name;

    /// <summary>Выбранный игрок выделен жирным.</summary>
    public FontWeight NameWeight => isSelected ? FontWeight.Bold : FontWeight.Normal;

    public bool IsT => player.FinalSide == Side.T;

    public string Initials => Texts.Initials(player.Name);

    public int Thrown => stats.Thrown;

    public int HeDamage => stats.HeDamage;

    public string HeText => stats.HeThrown == 0 ? "—" : $"{stats.HeDamage} / {stats.HeThrown}";

    public int FireDamage => stats.FireDamage;

    public string FireText => stats.FireThrown == 0 ? "—" : $"{stats.FireDamage} / {stats.FireThrown}";

    public int EnemiesBlinded => stats.EnemiesBlinded;

    public string FlashText => stats.FlashesThrown == 0 ? "—" : $"{stats.EnemiesBlinded} / {stats.FlashesThrown}";

    public string BlindSecondsText => stats.FlashesThrown == 0 ? "—" : stats.EnemyBlindSeconds.ToString("F1", CultureInfo.CurrentCulture);

    public int FlashKills => stats.FlashKills;

    public int TeammatesBlinded => stats.TeammatesBlinded;

    public bool HasTeammatesBlinded => stats.TeammatesBlinded > 0;

    public int TeamDamage => stats.TeamDamage;

    public bool HasTeamDamage => stats.TeamDamage > 0;

    public int Smokes => stats.SmokesThrown;
}

/// <summary>Бросок выбранного игрока: куда прилетел и чем закончился.</summary>
public sealed class GrenadeOutcomeRow(GrenadeOutcome outcome, string landZone, MatchData match)
{
    public GrenadeThrow Throw => outcome.Throw;

    public string Header =>
        $"{Loc.T("Раунд", "Round")} {Throw.Round} · {Texts.Time(Throw.ThrowTime)} · {Texts.GrenadeName(Throw.Type)} · {Texts.SideShort(Throw.ThrowerSide)}";

    public string RouteText
    {
        get
        {
            var target = Throw.Detonated ? Texts.ZoneOrUnknown(landZone) : Loc.T("не сработала", "did not go off");
            var route = $"{Texts.ZoneOrUnknown(Throw.ThrowerPlace)} → {target}";
            return LineupTexts.Technique(Throw.ThrowerMovement, Throw.ThrowerSpeed) is { Length: > 0 } technique
                ? $"{route} · {technique}"
                : route;
        }
    }

    public string OutcomeText
    {
        get
        {
            var parts = new List<string>();
            if (outcome.EnemyDamage > 0)
                parts.Add(Loc.T($"урон {outcome.EnemyDamage} (противников: {outcome.EnemiesHit})", $"damage {outcome.EnemyDamage} (enemies hit: {outcome.EnemiesHit})"));
            if (outcome.Kills > 0)
                parts.Add(Loc.T($"убийств: {outcome.Kills}", $"kills: {outcome.Kills}"));
            if (Throw.Type == GrenadeType.Flashbang)
            {
                parts.Add(outcome.EnemiesBlinded == 0
                    ? Loc.T("никого из противников не ослепила", "blinded no enemies")
                    : Loc.T($"ослепила {Victims(enemies: true)}", $"blinded {Victims(enemies: true)}"));
                if (outcome.FlashKills > 0)
                    parts.Add(Loc.T($"убито ослеплённых: {outcome.FlashKills}", $"flashed enemies killed: {outcome.FlashKills}"));
            }
            if (outcome.TeammatesBlinded > 0)
                parts.Add(Loc.T($"своих: {Victims(enemies: false)}", $"teammates: {Victims(enemies: false)}"));
            if (outcome.SelfBlinded)
                parts.Add(Loc.T("ослепила себя", "blinded self"));
            if (outcome.TeamDamage > 0)
                parts.Add(Loc.T($"урон своим {outcome.TeamDamage}", $"team damage {outcome.TeamDamage}"));
            if (parts.Count == 0 && Throw.Type is GrenadeType.HighExplosive or GrenadeType.Molotov or GrenadeType.Incendiary && Throw.Detonated)
                parts.Add(Loc.T("без урона", "no damage"));
            return string.Join(" · ", parts);
        }
    }

    public bool HasOutcome => OutcomeText.Length > 0;

    public bool PaidOff => outcome.PaidOff;

    public bool HurtTeam => outcome.HurtTeam;

    private string Victims(bool enemies) => string.Join(", ", Throw.FlashVictims
        .Where(v => !v.IsSelf && v.IsTeammate != enemies)
        .Select(v => $"{match.FindPlayer(v.SteamId)?.Name ?? "?"} {v.Duration.ToString("F1", CultureInfo.CurrentCulture)}{Loc.T(" с", " s")}"));
}

/// <summary>Раскидка в библиотеке. Название редактируется на месте и сохраняется при потере фокуса.</summary>
public sealed partial class LineupRowViewModel : ObservableObject
{
    private readonly Action<LineupRowViewModel, string> _rename;

    public LineupRowViewModel(Lineup lineup, Action<LineupRowViewModel, string> rename)
    {
        Lineup = lineup;
        _rename = rename;
        _title = lineup.Title;
    }

    public Lineup Lineup { get; }

    private string _title;

    public string Title
    {
        get => _title;
        set
        {
            var trimmed = value.Trim();
            if (trimmed.Length == 0 || trimmed == _title)
                return;
            SetProperty(ref _title, trimmed);
            _rename(this, trimmed);
        }
    }

    public string TypeText => Texts.GrenadeName(Lineup.Type);

    public string SideText => Texts.SideShort(Lineup.Side);

    public bool IsT => Lineup.Side == Side.T;

    public string MapName => Lineup.MapName;

    public string RouteText
    {
        get
        {
            var route = $"{Texts.ZoneOrUnknown(Lineup.FromZone)} → {Texts.ZoneOrUnknown(Lineup.ToZone)}";
            return LineupTexts.Technique(Lineup.Movement, Lineup.Speed) is { Length: > 0 } technique ? $"{route} · {technique}" : route;
        }
    }

    public string Command => LineupCommands.SetPos(Lineup);

    public string SourceText => Loc.T(
        $"{(Lineup.ThrowerName.Length > 0 ? Lineup.ThrowerName + " · " : "")}{Lineup.SourceFile}, раунд {Lineup.Round}",
        $"{(Lineup.ThrowerName.Length > 0 ? Lineup.ThrowerName + " · " : "")}{Lineup.SourceFile}, round {Lineup.Round}");

    public bool IsJumpThrow => Lineup.Movement?.HasFlag(MovementFlags.Airborne) == true;
}

public static class LineupTexts
{
    /// <summary>Скорость, выше которой бросок считается броском на бегу, units/s.</summary>
    private const float RunningSpeed = 100;

    /// <summary>Как бросали: «в прыжке», «присев», «шагом», «на бегу»; пусто — стоя или неизвестно.</summary>
    public static string Technique(MovementFlags? movement, float? speed)
    {
        if (movement is not { } m)
            return "";
        var parts = new List<string>();
        if (m.HasFlag(MovementFlags.Airborne))
            parts.Add(Loc.T("в прыжке", "jump throw"));
        if (m.HasFlag(MovementFlags.Ducking))
            parts.Add(Loc.T("присев", "crouching"));
        if (m.HasFlag(MovementFlags.Walking) && speed is > 5)
            parts.Add(Loc.T("шагом", "walking"));
        else if (speed is > RunningSpeed && !m.HasFlag(MovementFlags.Airborne))
            parts.Add(Loc.T("на бегу", "running"));
        return string.Join(", ", parts);
    }
}
