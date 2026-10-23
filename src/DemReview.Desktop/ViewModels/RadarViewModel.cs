using System.Collections.ObjectModel;
using System.Numerics;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemReview.Application;
using DemReview.Application.Context;
using DemReview.Application.Damage;
using DemReview.Application.Filters;
using DemReview.Application.Grenades;
using DemReview.Application.Heatmaps;
using DemReview.Application.Localization;
using DemReview.Application.Maps;
using DemReview.Application.Patterns;
using DemReview.Application.Routes;
using DemReview.Application.Situations;
using DemReview.Desktop.Controls;
using DemReview.Domain;
using Microsoft.Extensions.Logging;
using Point = Avalonia.Point;

namespace DemReview.Desktop.ViewModels;

public enum HeatmapMode
{
    Positions,
    Deaths,
}

/// <summary>
/// Вкладка «Радар»: подложка карты, слои (маршруты, гранаты, смерти, тепловая карта) выбранного игрока
/// с учётом фильтров, частые маршруты, раскидки и таймлайн раунда с воспроизведением, киллфидом и уроном.
/// </summary>
public partial class RadarViewModel : ViewModelBase
{
    /// <summary>Радиус облака дыма и зоны огня, игровые units; время действия, секунды.</summary>
    private const float SmokeRadius = 144;
    private const float FireRadius = 120;
    private const float SmokeSeconds = 18;
    private const float FireSeconds = 7;

    /// <summary>Сколько секунд игрового времени убийство остаётся в киллфиде и сколько строк в нём максимум.</summary>
    private const float KillFeedSeconds = 7;
    private const int KillFeedMaxEntries = 6;

    /// <summary>Сколько секунд после попадания виден урон; попадания с меньшим интервалом суммируются.</summary>
    private const float DamagePopupSeconds = 1.5f;

    private readonly IRouteAnalysisService _routeAnalysis;
    private readonly IGrenadeAnalysisService _grenadeAnalysis;
    private readonly IRoundContextService _contexts;
    private readonly IHeatmapService _heatmaps;
    private readonly IMapConfigProvider _mapConfigs;
    private readonly IMapImageProvider _mapImages;
    private readonly IMapCoordinateConverter _converter;
    private readonly ISettingsService _settings;
    private readonly IPlatformService _platform;
    private readonly ILogger<RadarViewModel> _logger;

    private MatchData? _match;
    private IMatchContext? _context;
    private MapConfig? _map;
    private ulong? _steamId;
    private RouteAnalysis? _routes;
    private GrenadeAnalysis? _grenades;
    private IReadOnlyList<DamageHit> _hits = [];
    private AnalysisFilter _filter = AnalysisFilter.None;
    private int _analysisVersion;
    private int _heatmapVersion;
    private bool _updatingSelection;
    private IReadOnlySet<int>? _focusRounds;
    private string _killFeedKey = "";

    public RadarViewModel(
        IRouteAnalysisService routeAnalysis,
        IGrenadeAnalysisService grenadeAnalysis,
        IRoundContextService contexts,
        IHeatmapService heatmaps,
        IMapConfigProvider mapConfigs,
        IMapImageProvider mapImages,
        IMapCoordinateConverter converter,
        ISettingsService settings,
        IPlatformService platform,
        ILogger<RadarViewModel> logger)
    {
        _routeAnalysis = routeAnalysis;
        _grenadeAnalysis = grenadeAnalysis;
        _contexts = contexts;
        _heatmaps = heatmaps;
        _mapConfigs = mapConfigs;
        _mapImages = mapImages;
        _converter = converter;
        _settings = settings;
        _platform = platform;
        _logger = logger;
        Timeline.TickChanged += _ => RebuildLayers();
    }

    public TimelineViewModel Timeline { get; } = new();

    [ObservableProperty]
    public partial Bitmap? MapImage { get; private set; }

    [ObservableProperty]
    public partial string MapTitle { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMapHint))]
    public partial string? MapHint { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<RadarRoute> Routes { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<RadarGrenade> Grenades { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<Point> Deaths { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<RadarPlayerMarker>? Players { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<RadarDamageText>? DamageTexts { get; private set; }

    /// <summary>Киллфид момента таймлайна: последние убийства раунда, убийства и смерть игрока выделены.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasKillFeed))]
    public partial IReadOnlyList<KillFeedEntry> KillFeed { get; private set; } = [];

    [ObservableProperty]
    public partial Bitmap? Heatmap { get; private set; }

    [ObservableProperty]
    public partial IReadOnlySet<int> HighlightedRounds { get; private set; } = new HashSet<int>();

    [ObservableProperty]
    public partial IReadOnlySet<int> HighlightedGrenades { get; private set; } = new HashSet<int>();

    [ObservableProperty]
    public partial Func<Point, Point>? RadarToWorld { get; private set; }

    [ObservableProperty]
    public partial bool ShowRoutes { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowGrenades { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowDeaths { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowHeatmap { get; set; }

    [ObservableProperty]
    public partial FilterOption SelectedHeatmapMode { get; set; } = HeatmapModes[0];

    [ObservableProperty]
    public partial RouteRoundRow? SelectedRound { get; set; }

    [ObservableProperty]
    public partial FrequentRouteRow? SelectedFrequentRoute { get; set; }

    [ObservableProperty]
    public partial GrenadeClusterRow? SelectedCluster { get; set; }

    [ObservableProperty]
    public partial GrenadeThrowRow? SelectedThrow { get; set; }

    [ObservableProperty]
    public partial string FlashSummaryText { get; private set; } = "";

    public static IReadOnlyList<FilterOption> HeatmapModes { get; } =
    [
        new("позиции", "positions", HeatmapMode.Positions),
        new("смерти", "deaths", HeatmapMode.Deaths),
    ];

    public ObservableCollection<RouteRoundRow> RoundRows { get; } = [];

    public ObservableCollection<FrequentRouteRow> FrequentRoutes { get; } = [];

    public ObservableCollection<ZoneTimingRow> ZoneTimings { get; } = [];

    public ObservableCollection<GrenadeClusterRow> GrenadeClusters { get; } = [];

    public ObservableCollection<GrenadeThrowRow> GrenadeThrows { get; } = [];

    public ObservableCollection<GrenadeSummaryRow> GrenadeSummary { get; } = [];

    public bool HasMapHint => MapHint is not null;

    public bool HasKillFeed => KillFeed.Count > 0;

    partial void OnShowHeatmapChanged(bool value) => _ = RebuildHeatmapAsync();
    partial void OnShowDeathsChanged(bool value) => RebuildLayers();
    partial void OnSelectedHeatmapModeChanged(FilterOption value) => _ = RebuildHeatmapAsync();

    partial void OnSelectedRoundChanged(RouteRoundRow? value) => OnSelectionChanged(value);
    partial void OnSelectedFrequentRouteChanged(FrequentRouteRow? value) => OnSelectionChanged(value);
    partial void OnSelectedClusterChanged(GrenadeClusterRow? value) => OnSelectionChanged(value);
    partial void OnSelectedThrowChanged(GrenadeThrowRow? value) => OnSelectionChanged(value);

    /// <summary>Новая демка: конфиг карты, изображение подложки, контекст матча.</summary>
    public void SetMatch(MatchData match)
    {
        _match = match;
        var s = _settings.Current;
        _context = _contexts.Create(match, new EconomyOptions(s.BuyEcoMaxEquipment, s.BuyFullMinEquipment));
        _hits = DamageAnalysis.EffectiveHits(match);
        _map = _mapConfigs.Find(match.MapName);
        var hints = new List<string>();

        if (_map is null)
        {
            _map = _converter.FitToPositions(match.MapName,
                match.Tracks.Values.SelectMany(t => t.Samples).Select(sample => sample.Position));
            hints.Add(Loc.T($"Карта {match.MapName} не описана в maps.json — масштаб подобран по позициям игроков, подложки нет.",
                $"Map {match.MapName} is not described in maps.json — the scale is fitted to player positions, no background image."));
            MapImage = null;
        }
        else
        {
            MapImage = LoadImage(match.MapName, hints);
        }

        var map = _map;
        RadarToWorld = p =>
        {
            var world = _converter.ToWorld(map, new Vector2((float)p.X, (float)p.Y));
            return new Point(world.X, world.Y);
        };
        MapTitle = map.DisplayName;
        MapHint = hints.Count > 0 ? string.Join(Environment.NewLine, hints) : null;
        _filter = AnalysisFilter.None;
        _steamId = null;
        Clear();
    }

    /// <summary>Анализ маршрутов и гранат выбранного игрока — в фоновом потоке.</summary>
    public async Task ShowPlayerAsync(ulong? steamId)
    {
        var version = ++_analysisVersion;
        _steamId = steamId;
        if (_match is not { } match || steamId is not { } id)
        {
            Clear();
            return;
        }

        var current = _settings.Current;
        var clusterOptions = new GrenadeClusterOptions(current.GrenadeClusterThrowDistance, current.GrenadeClusterLandDistance);
        var (routes, grenades) = await Task.Run(() =>
            (_routeAnalysis.Analyze(match, id), _grenadeAnalysis.Analyze(match, id, clusterOptions)));
        if (version != _analysisVersion)
            return;

        ClearFocus();
        _routes = routes;
        _grenades = grenades;
        FillLists();
        UpdateTimelineRound();
        RebuildLayers();
        await RebuildHeatmapAsync();
    }

    /// <summary>Фильтры изменились: пересобрать списки, слои и тепловую карту.</summary>
    public async Task SetFilterAsync(AnalysisFilter filter)
    {
        _filter = filter;
        ClearFocus();
        FillLists();
        UpdateTimelineRound();
        RebuildLayers();
        await RebuildHeatmapAsync();
    }

    [RelayCommand]
    private void OpenMapsFolder() => _platform.OpenFolder(_platform.MapsDirectory);

    [RelayCommand]
    private void ClearSelection()
    {
        ClearFocus();
        OnSelectionChanged(null);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFocus))]
    public partial string? FocusText { get; private set; }

    public bool HasFocus => FocusText is not null;

    /// <summary>Показать раунды-примеры паттерна наложенными друг на друга (и связанные гранаты).</summary>
    public void FocusPattern(Pattern pattern)
    {
        _focusRounds = pattern.Rounds.ToHashSet();
        FocusText = Loc.T($"Раунды-примеры: {string.Join(", ", pattern.Rounds)} — {pattern.Text}",
            $"Example rounds: {string.Join(", ", pattern.Rounds)} — {pattern.Text}");
        OnSelectionChanged(null);
        HighlightedRounds = _focusRounds;
        HighlightedGrenades = pattern.GrenadeIds.ToHashSet();
        UpdateTimelineRound();
        RebuildLayers();
    }

    private void ClearFocus()
    {
        if (_focusRounds is null)
            return;
        _focusRounds = null;
        FocusText = null;
        UpdateTimelineRound();
        RebuildLayers();
    }

    private bool IncludesRound(int round) =>
        _context is not null && _steamId is { } id && _filter.IncludesRound(_context, id, round);

    private bool IncludesGrenade(GrenadeThrow g) => IncludesRound(g.Round) && _filter.IncludesGrenade(g);

    private Bitmap? LoadImage(string mapName, List<string> hints)
    {
        if (_mapImages.FindImage(mapName) is not { } path)
        {
            var file = Path.GetFileName(_mapImages.ExpectedPath(mapName));
            hints.Add(Loc.T($"Нет изображения карты. Положите {file} в папку {_platform.MapsDirectory}",
                $"No map image. Put {file} into {_platform.MapsDirectory}"));
            return null;
        }

        try
        {
            return new Bitmap(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось загрузить изображение карты {Path}", path);
            hints.Add(Loc.T($"Не удалось загрузить {path}: {ex.Message}", $"Could not load {path}: {ex.Message}"));
            return null;
        }
    }

    private void Clear()
    {
        _routes = null;
        _grenades = null;
        RoundRows.Clear();
        FrequentRoutes.Clear();
        ZoneTimings.Clear();
        GrenadeClusters.Clear();
        GrenadeThrows.Clear();
        GrenadeSummary.Clear();
        FlashSummaryText = "";
        Routes = [];
        Grenades = [];
        Deaths = [];
        Players = null;
        DamageTexts = null;
        SetKillFeed([]);
        Heatmap = null;
        Timeline.SetRound(null, 64);
        OnSelectionChanged(null);
    }

    /// <summary>Списки правой панели с учётом фильтра. Частые маршруты и тайминги — по всем раундам игрока.</summary>
    private void FillLists()
    {
        if (_routes is not { } routes || _grenades is not { } grenades || _match is not { } match)
            return;

        OnSelectionChanged(null);

        RoundRows.Clear();
        foreach (var round in routes.Rounds.Where(r => IncludesRound(r.Round)))
            RoundRows.Add(new RouteRoundRow(round));

        FrequentRoutes.Clear();
        foreach (var route in routes.FrequentRoutes)
            FrequentRoutes.Add(new FrequentRouteRow(route));

        ZoneTimings.Clear();
        foreach (var timing in routes.ZoneTimings.Where(t => t.Rounds >= 2))
            ZoneTimings.Add(new ZoneTimingRow(timing));

        GrenadeClusters.Clear();
        foreach (var cluster in grenades.Clusters.Where(c => c.Throws.Any(IncludesGrenade)))
            GrenadeClusters.Add(new GrenadeClusterRow(cluster, grenades.Damage));

        GrenadeThrows.Clear();
        foreach (var t in grenades.Throws.Where(IncludesGrenade))
            GrenadeThrows.Add(new GrenadeThrowRow(t, grenades.LandZones.GetValueOrDefault(t.Id, ""), grenades.Damage.GetValueOrDefault(t.Id), match));

        GrenadeSummary.Clear();
        foreach (var s in grenades.Summary)
            GrenadeSummary.Add(new GrenadeSummaryRow(s));

        var f = grenades.Flashes;
        var damage = grenades.Damage.Values.ToList();
        FlashSummaryText = (f.Thrown == 0
            ? Loc.T("Флешек не бросал.", "No flashbangs thrown.")
            : Loc.T($"Флешек: {f.Thrown}. Ослепил противников: {f.EnemiesBlinded} ({f.EnemyBlindSeconds:F1} с), " +
                    $"союзников: {f.TeammatesBlinded} ({f.TeammateBlindSeconds:F1} с), себя: {f.SelfBlinded}. " +
                    $"Без ослеплённых противников: {f.WithoutEnemyVictims}.",
                $"Flashbangs: {f.Thrown}. Enemies blinded: {f.EnemiesBlinded} ({f.EnemyBlindSeconds:F1} s), " +
                $"teammates: {f.TeammatesBlinded} ({f.TeammateBlindSeconds:F1} s), self: {f.SelfBlinded}. " +
                $"Without blinded enemies: {f.WithoutEnemyVictims}.")) +
            (damage.Count == 0 ? "" : " " + Loc.T(
                $"Урон гранатами (HE и огонь): {damage.Sum(d => d.EnemyDamage)} по противникам, {damage.Sum(d => d.TeamDamage)} по своим.",
                $"Grenade damage (HE and fire): {damage.Sum(d => d.EnemyDamage)} to enemies, {damage.Sum(d => d.TeamDamage)} to teammates."));
    }

    /// <summary>Раунд таймлайна: раунд из фильтра, иначе выбранный в списке раундов.</summary>
    private void UpdateTimelineRound()
    {
        // В режиме раундов-примеров паттерна таймлайн выключен: раунды показываются наложенными.
        var number = _focusRounds is not null ? null : _filter.Round ?? SelectedRound?.Round;
        var round = number is { } n && _match?.FindRound(n) is { } r && _steamId is not null ? r : null;
        Timeline.SetRound(round, _match?.TickRate ?? 64);
    }

    private Point ToRadar(Vector3 world)
    {
        var v = _converter.ToRadar(_map!, world);
        return new Point(v.X, v.Y);
    }

    /// <summary>Пересборка векторных слоёв. В режиме таймлайна — только раунд таймлайна до текущего тика.</summary>
    private void RebuildLayers()
    {
        if (_map is not { } map || _match is not { } match || _steamId is not { } steamId)
            return;

        var timelineRound = Timeline.Round;
        var tick = timelineRound is null ? int.MaxValue : (int)Timeline.CurrentTick;
        bool RoundVisible(int round) => timelineRound is { } r ? round == r.Number
            : _focusRounds is { } focus ? focus.Contains(round)
            : IncludesRound(round);

        var tIndex = 0;
        var ctIndex = 0;
        Routes = _routes is null ? [] : _routes.Rounds
            .Where(r => RoundVisible(r.Round))
            .Select(r =>
            {
                var color = r.Side == Side.CT ? Palette.Ct(ctIndex++) : Palette.T(tIndex++);
                var path = r.Path.Where(s => s.Tick <= tick).ToList();
                return new RadarRoute(r.Round, path.Select(s => ToRadar(s.Position)).ToList(),
                    path.Select(s => _converter.IsLowerLevel(map, s.Position)).ToList(), color, r.Died && path.Count == r.Path.Count);
            })
            .ToList();

        var grenadeDamage = _grenades?.Damage;
        Grenades = _grenades is null ? [] : _grenades.Throws
            .Where(g => RoundVisible(g.Round) && _filter.IncludesGrenade(g) && g.ThrowTick <= tick)
            .Select(g =>
            {
                var landed = g.DetonatePosition is { } land && g.DetonateTick <= tick && !EffectExpired(g, tick) ? ToRadar(land) : (Point?)null;
                return new RadarGrenade(
                    g.Id,
                    g.Round,
                    KindOf(g.Type),
                    ToRadar(g.ThrowerPosition),
                    landed,
                    (g.Type == GrenadeType.Smoke ? SmokeRadius : FireRadius) / map.Scale,
                    Palette.Grenade(g.Type),
                    landed is null ? null : GrenadeDamageLabel(grenadeDamage?.GetValueOrDefault(g.Id), tick),
                    timelineRound is null ? null : FlyingAt(g, tick));
            })
            .ToList();

        Deaths = !ShowDeaths ? [] : match.Kills
            .Where(k => k.VictimSteamId == steamId && RoundVisible(k.Round) && k.Tick <= tick)
            .Select(k => ToRadar(k.VictimPosition))
            .ToList();

        if (timelineRound is null)
        {
            Players = null;
            DamageTexts = null;
            SetKillFeed([]);
            Timeline.ContextText = "";
        }
        else
        {
            Players = BuildMarkers(match, timelineRound.Number, tick, steamId);
            DamageTexts = BuildDamageTexts(match, timelineRound.Number, tick, steamId);
            SetKillFeed(BuildKillFeed(match, timelineRound.Number, tick, steamId));
            var moment = _context!.At(steamId, timelineRound.Number, tick);
            Timeline.ContextText = MomentText(moment, DamageSoFar(timelineRound.Number, tick, steamId));
        }
    }

    /// <summary>
    /// Подпись урона гранаты у места взрыва. В режиме таймлайна — только урон, уже нанесённый к текущему тику
    /// (огонь наносит урон постепенно).
    /// </summary>
    private static string? GrenadeDamageLabel(GrenadeDamage? damage, int tick)
    {
        if (damage is null)
            return null;
        var hits = damage.Hits.Where(h => h.Tick <= tick).ToList();
        var enemy = hits.Where(h => !h.IsTeammate).Sum(h => h.Damage);
        var team = hits.Where(h => h.IsTeammate).Sum(h => h.Damage);
        if (enemy == 0 && team == 0)
            return null;
        var victims = hits.Where(h => !h.IsTeammate).Select(h => h.VictimSteamId).Distinct().Count();
        var text = enemy > 0 ? $"−{enemy}{(victims > 1 ? $" ×{victims}" : "")}" : "";
        if (team > 0)
            text += (text.Length > 0 ? " · " : "") + Loc.T($"своим −{team}", $"team −{team}");
        return text;
    }

    /// <summary>
    /// Где летящая граната в этот тик. Траектория в демке не записывается — известны только точка вылета и детонации,
    /// поэтому позиция интерполируется по прямой (сверху полёт без отскоков почти прямой).
    /// </summary>
    private Point? FlyingAt(GrenadeThrow g, int tick)
    {
        if (g.DetonateTick is not { } detonate || g.DetonatePosition is not { } land || tick < g.ThrowTick || tick >= detonate)
            return null;
        var t = (tick - g.ThrowTick) / (float)Math.Max(1, detonate - g.ThrowTick);
        return ToRadar(Vector3.Lerp(g.ProjectileStart, land, t));
    }

    /// <summary>Сколько секунд до броска начинается просмотр броска на таймлайне.</summary>
    private const float WatchLeadSeconds = 2;

    /// <summary>Подходит ли бросок под текущий фильтр (иначе на радаре его не будет видно).</summary>
    public bool Shows(GrenadeThrow g) => (_filter.Round is null || _filter.Round == g.Round) && IncludesGrenade(g);

    /// <summary>
    /// Открыть раунд броска на таймлайне за пару секунд до броска, на паузе, с выделенной гранатой —
    /// чтобы запустить воспроизведение и посмотреть полёт. false — раунд открыть не удалось.
    /// </summary>
    public bool WatchThrow(GrenadeThrow g)
    {
        if (_match is not { } match || _steamId is null)
            return false;

        ClearFocus();
        if (_filter.Round is null)
        {
            if (RoundRows.FirstOrDefault(r => r.Round == g.Round) is not { } row)
                return false;
            SelectedRound = row;
        }
        if (Timeline.Round?.Number != g.Round)
            return false;

        HighlightedRounds = new HashSet<int> { g.Round };
        HighlightedGrenades = new HashSet<int> { g.Id };
        Timeline.Seek(g.ThrowTick - (int)(WatchLeadSeconds * match.TickRate));
        return true;
    }

    /// <summary>В режиме таймлайна дым исчезает через ~18 с, огонь — через ~7 с.</summary>
    private bool EffectExpired(GrenadeThrow g, int tick)
    {
        if (Timeline.Round is null || g.DetonateTick is not { } detonate)
            return false;
        var seconds = g.Type switch
        {
            GrenadeType.Smoke => SmokeSeconds,
            GrenadeType.Molotov or GrenadeType.Incendiary => FireSeconds,
            _ => 2f,
        };
        return tick > detonate + seconds * (_match?.TickRate ?? 64);
    }

    /// <summary>
    /// Маркеры игроков: живые — с интерполяцией между сэмплами (плавное движение при воспроизведении),
    /// погибшие в раунде — крестиком на месте смерти. У каждого — подсказка для наведения мышью.
    /// </summary>
    private List<RadarPlayerMarker> BuildMarkers(MatchData match, int round, int tick, ulong selected)
    {
        var markers = new List<RadarPlayerMarker>();
        var deaths = match.Kills.Where(k => k.Round == round && k.Tick <= tick).ToList();
        foreach (var player in match.Players)
        {
            var side = player.SideInRound(round);
            if (side == Side.None)
                continue;

            var isSelected = player.SteamId == selected;
            var color = isSelected ? Color.FromRgb(255, 255, 255) : Palette.Side(side);

            if (!_context!.IsAlive(player.SteamId, round, tick))
            {
                if (deaths.LastOrDefault(k => k.VictimSteamId == player.SteamId) is { } death)
                {
                    var killer = death.AttackerSteamId is { } a && a != player.SteamId ? match.FindPlayer(a)?.Name : null;
                    var how = killer is null ? Texts.Weapon(death.Weapon) : $"{killer}, {Texts.Weapon(death.Weapon)}";
                    markers.Add(new RadarPlayerMarker(ToRadar(death.VictimPosition), 0, color, isSelected, "", player.Name,
                        Loc.T($"{player.Name} — погиб ({how})", $"{player.Name} — killed ({how})"), IsDead: true));
                }
                continue;
            }

            var samples = match.TrackOf(player.SteamId).Samples;
            var index = LastAtOrBefore(samples, tick);
            if (index < 0 || samples[index].Round != round)
                continue;

            var sample = samples[index];
            var (position, yaw) = Interpolate(samples, index, round, tick);
            var label = $"{player.Name} · {Texts.Weapon(sample.Weapon)} · {sample.Health} HP";
            markers.Add(new RadarPlayerMarker(ToRadar(position), yaw, color, isSelected, isSelected ? label : "", player.Name,
                $"{label} · ${sample.Money}", IsDead: false));
        }
        return markers;
    }

    /// <summary>Позиция и взгляд игрока в тике: интерполяция между соседними сэмплами раунда.</summary>
    private static (Vector3 Position, float Yaw) Interpolate(IReadOnlyList<PlayerSample> samples, int index, int round, int tick)
    {
        var a = samples[index];
        if (index + 1 < samples.Count && samples[index + 1] is { } b && b.Round == round && b.Tick > a.Tick)
        {
            var t = Math.Clamp((tick - a.Tick) / (float)(b.Tick - a.Tick), 0, 1);
            return (Vector3.Lerp(a.Position, b.Position, t), LerpAngle(a.Yaw, b.Yaw, t));
        }
        return (a.Position, a.Yaw);
    }

    private static Vector3? PositionAt(MatchData match, ulong steamId, int round, int tick)
    {
        var samples = match.TrackOf(steamId).Samples;
        var index = LastAtOrBefore(samples, tick);
        return index >= 0 && samples[index].Round == round ? Interpolate(samples, index, round, tick).Position : null;
    }

    /// <summary>
    /// Урон с участием игрока за последние ~1,5 с: число над жертвой («−27»), серия попаданий по одной цели
    /// суммируется. Нанесённый — жёлтый (по союзнику — оранжевый), полученный — красный.
    /// </summary>
    private List<RadarDamageText> BuildDamageTexts(MatchData match, int round, int tick, ulong selected)
    {
        var window = (int)(DamagePopupSeconds * match.TickRate);
        var texts = new List<RadarDamageText>();
        var pairs = _hits
            .Where(h => h.Round == round && h.Tick <= tick && h.Damage > 0 &&
                        (h.AttackerSteamId == selected || h.VictimSteamId == selected))
            .GroupBy(h => (h.AttackerSteamId, h.VictimSteamId));

        foreach (var pair in pairs)
        {
            var hits = pair.OrderBy(h => h.Tick).ToList();
            var last = hits[^1];
            if (tick - last.Tick > window)
                continue;

            // Серия: попадания с интервалом не больше окна.
            var sum = last.Damage;
            for (var i = hits.Count - 2; i >= 0 && hits[i + 1].Tick - hits[i].Tick <= window; i--)
                sum += hits[i].Damage;

            var victimKill = match.Kills.FirstOrDefault(k => k.Round == round && k.VictimSteamId == last.VictimSteamId && k.Tick <= tick);
            if ((victimKill?.VictimPosition ?? PositionAt(match, last.VictimSteamId, round, tick)) is not { } position)
                continue;

            var age = (tick - last.Tick) / (double)window;
            var dealt = last.AttackerSteamId == selected && last.VictimSteamId != selected;
            var color = !dealt ? Color.FromRgb(255, 85, 85)
                : last.IsTeammate ? Color.FromRgb(255, 150, 50)
                : Color.FromRgb(255, 220, 70);
            texts.Add(new RadarDamageText(ToRadar(position), $"−{sum}", color, age));
        }
        return texts;
    }

    /// <summary>Урон игрока в раунде к текущему тику: нанесённый противникам и полученный.</summary>
    private (int Dealt, int Taken) DamageSoFar(int round, int tick, ulong selected)
    {
        var hits = _hits.Where(h => h.Round == round && h.Tick <= tick).ToList();
        return (hits.Where(h => h.AttackerSteamId == selected && !h.IsTeammate).Sum(h => h.Damage),
            hits.Where(h => h.VictimSteamId == selected).Sum(h => h.Damage));
    }

    /// <summary>Киллфид как в игре: убийства за последние ~7 с, новые внизу.</summary>
    private static List<KillFeedEntry> BuildKillFeed(MatchData match, int round, int tick, ulong selected)
    {
        var window = (int)(KillFeedSeconds * match.TickRate);
        PlayerInfo? Player(ulong? id) => id is { } v ? match.FindPlayer(v) : null;
        IBrush Brush(PlayerInfo? p) => new SolidColorBrush(p is null ? Colors.White : Palette.Side(p.SideInRound(round)));

        return match.Kills
            .Where(k => k.Round == round && k.Tick <= tick && tick - k.Tick <= window)
            .OrderBy(k => k.Tick)
            .TakeLast(KillFeedMaxEntries)
            .Select(k =>
            {
                var attacker = k.AttackerSteamId != k.VictimSteamId ? Player(k.AttackerSteamId) : null;
                var assister = Player(k.AssisterSteamId);
                var victim = Player(k.VictimSteamId);
                return new KillFeedEntry(
                    k.Tick,
                    attacker?.Name ?? "",
                    Brush(attacker),
                    assister is null ? "" : $"+ {assister.Name}",
                    Brush(assister),
                    Texts.Weapon(k.Weapon),
                    k.Headshot,
                    victim?.Name ?? "?",
                    Brush(victim),
                    k.AttackerSteamId == selected && k.VictimSteamId != selected,
                    k.VictimSteamId == selected,
                    k.AssisterSteamId == selected);
            })
            .ToList();
    }

    /// <summary>Обновлять список только при изменении состава — иначе шаблоны пересоздаются каждый кадр.</summary>
    private void SetKillFeed(List<KillFeedEntry> entries)
    {
        var key = string.Join(",", entries.Select(e => $"{e.Tick}:{e.Victim}"));
        if (key == _killFeedKey)
            return;
        _killFeedKey = key;
        KillFeed = entries;
    }

    private static int LastAtOrBefore(IReadOnlyList<PlayerSample> samples, int tick)
    {
        int lo = 0, hi = samples.Count - 1, result = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (samples[mid].Tick <= tick)
            {
                result = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return result;
    }

    private static float LerpAngle(float a, float b, float t)
    {
        var delta = ((b - a) % 360 + 540) % 360 - 180;
        return a + delta * t;
    }

    private static string MomentText(MomentContext m, (int Dealt, int Taken) damage)
    {
        var bomb = m.Bomb switch
        {
            BombState.Planted => Loc.T($"бомба заложена на {m.BombSite}", $"bomb planted on {m.BombSite}"),
            BombState.Defused => Loc.T("бомба обезврежена", "bomb defused"),
            BombState.Exploded => Loc.T("бомба взорвалась", "bomb exploded"),
            _ => Loc.T("бомба не заложена", "bomb not planted"),
        };
        var state = m.PlayerAlive
            ? Loc.T($"${m.Money}, экип. ${m.Equipment}", $"${m.Money}, equipment ${m.Equipment}")
            : Loc.T("погиб", "dead");
        return Loc.T(
            $"{Labels.Side(m.Side)} · {m.Matchup} · {state} · урон: нанёс {damage.Dealt}, получил {damage.Taken} · " +
            $"закупка {Labels.Buy(m.TeamBuy)} против {Labels.Buy(m.EnemyBuy)} · {bomb} · счёт {m.ScoreUs}:{m.ScoreThem}",
            $"{Labels.Side(m.Side)} · {m.Matchup} · {state} · damage: dealt {damage.Dealt}, taken {damage.Taken} · " +
            $"{Labels.Buy(m.TeamBuy)} vs {Labels.Buy(m.EnemyBuy)} · {bomb} · score {m.ScoreUs}:{m.ScoreThem}");
    }

    /// <summary>Тепловая карта по раундам под фильтром: позиции игрока или места его смертей.</summary>
    private async Task RebuildHeatmapAsync()
    {
        var version = ++_heatmapVersion;
        if (!ShowHeatmap || _match is not { } match || _map is not { } map || _steamId is not { } steamId)
        {
            Heatmap = null;
            return;
        }

        var deaths = SelectedHeatmapMode.Value is HeatmapMode.Deaths;
        var rounds = match.Rounds.Select(r => r.Number).Where(IncludesRound).ToHashSet();
        var data = await Task.Run(() =>
        {
            var points = deaths
                ? match.Kills.Where(k => k.VictimSteamId == steamId && rounds.Contains(k.Round)).Select(k => k.VictimPosition)
                : match.TrackOf(steamId).Samples.Where(s => rounds.Contains(s.Round)).Select(s => s.Position);
            var radarPoints = points.Select(p => _converter.ToRadar(map, p));
            return _heatmaps.Build(radarPoints, new HeatmapOptions(Size: 256, BlurRadius: deaths ? 6 : 3));
        });

        if (version == _heatmapVersion)
            Heatmap = data.PointCount == 0 ? null : HeatmapRenderer.Render(data);
    }

    /// <summary>Выбор в одном списке снимает выбор в остальных и задаёт подсветку на радаре.</summary>
    private void OnSelectionChanged(object? selected)
    {
        if (_updatingSelection)
            return;

        if (selected is not null)
            ClearFocus();

        _updatingSelection = true;
        try
        {
            if (selected is not RouteRoundRow) SelectedRound = null;
            if (selected is not FrequentRouteRow) SelectedFrequentRoute = null;
            if (selected is not GrenadeClusterRow) SelectedCluster = null;
            if (selected is not GrenadeThrowRow) SelectedThrow = null;
        }
        finally
        {
            _updatingSelection = false;
        }

        (HighlightedRounds, HighlightedGrenades) = selected switch
        {
            RouteRoundRow r => (new HashSet<int> { r.Round }, new HashSet<int>()),
            FrequentRouteRow f => (f.Route.Rounds.ToHashSet(), new HashSet<int>()),
            GrenadeClusterRow c => (c.Cluster.Rounds.ToHashSet(), c.Cluster.Throws.Select(t => t.Id).ToHashSet()),
            GrenadeThrowRow t => (new HashSet<int> { t.Throw.Round }, new HashSet<int> { t.Throw.Id }),
            _ => ((IReadOnlySet<int>)new HashSet<int>(), (IReadOnlySet<int>)new HashSet<int>()),
        };

        // Выбор раунда в списке открывает его на таймлайне (если раунд не задан фильтром).
        if (_filter.Round is null)
        {
            UpdateTimelineRound();
            RebuildLayers();
        }
    }

    private static RadarGrenadeKind KindOf(GrenadeType type) => type switch
    {
        GrenadeType.HighExplosive => RadarGrenadeKind.HighExplosive,
        GrenadeType.Flashbang => RadarGrenadeKind.Flash,
        GrenadeType.Smoke => RadarGrenadeKind.Smoke,
        GrenadeType.Decoy => RadarGrenadeKind.Decoy,
        _ => RadarGrenadeKind.Fire,
    };

    /// <summary>Цвета слоёв: маршруты T — тёплые, CT — холодные; гранаты — по типу.</summary>
    private static class Palette
    {
        public static Color T(int i) => FromHsv(18 + (i * 37) % 50, 0.85, 1.0);

        public static Color Ct(int i) => FromHsv(185 + (i * 41) % 60, 0.75, 1.0);

        /// <summary>Цвет стороны для маркеров игроков и имён в киллфиде.</summary>
        public static Color Side(Side side) => side == Domain.Side.CT ? Color.FromRgb(90, 170, 255) : Color.FromRgb(255, 170, 60);

        public static Color Grenade(GrenadeType type) => type switch
        {
            GrenadeType.HighExplosive => Color.FromRgb(255, 80, 80),
            GrenadeType.Flashbang => Color.FromRgb(250, 240, 120),
            GrenadeType.Smoke => Color.FromRgb(205, 212, 222),
            GrenadeType.Molotov or GrenadeType.Incendiary => Color.FromRgb(255, 140, 26),
            _ => Color.FromRgb(110, 215, 110),
        };

        private static Color FromHsv(double h, double s, double v)
        {
            var c = v * s;
            var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
            var m = v - c;
            var (r, g, b) = (h / 60) switch
            {
                < 1 => (c, x, 0d),
                < 2 => (x, c, 0d),
                < 3 => (0d, c, x),
                < 4 => (0d, x, c),
                < 5 => (x, 0d, c),
                _ => (c, 0d, x),
            };
            return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
        }
    }
}

/// <summary>Строка киллфида.</summary>
/// <param name="IsSelectedKill">Убийство совершил анализируемый игрок.</param>
/// <param name="IsSelectedDeath">Погиб анализируемый игрок.</param>
/// <param name="IsSelectedAssist">Анализируемый игрок помог в убийстве.</param>
public sealed record KillFeedEntry(
    int Tick,
    string Attacker,
    IBrush AttackerBrush,
    string Assist,
    IBrush AssistBrush,
    string Weapon,
    bool Headshot,
    string Victim,
    IBrush VictimBrush,
    bool IsSelectedKill,
    bool IsSelectedDeath,
    bool IsSelectedAssist)
{
    private static readonly IBrush Highlight = new SolidColorBrush(Color.FromRgb(230, 60, 60));
    private static readonly IBrush AssistHighlight = new SolidColorBrush(Color.FromArgb(150, 230, 60, 60));
    private static readonly IBrush Plain = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
    private static readonly IBrush NormalBackground = new SolidColorBrush(Color.FromArgb(200, 12, 14, 18));
    private static readonly IBrush DeathBackground = new SolidColorBrush(Color.FromArgb(225, 75, 16, 16));

    public bool HasAttacker => Attacker.Length > 0;

    public bool HasAssist => Assist.Length > 0;

    public IBrush BorderBrush => IsSelectedKill || IsSelectedDeath ? Highlight : IsSelectedAssist ? AssistHighlight : Plain;

    public IBrush Background => IsSelectedDeath ? DeathBackground : NormalBackground;
}

public sealed class RouteRoundRow(RoundRoute route)
{
    public int Round => route.Round;

    public string Header => $"{Loc.T("Раунд", "Round")} {route.Round} · {Texts.SideShort(route.Side)}{(route.Died ? Loc.T(" · погиб", " · died") : "")}";

    public string ZonesText => route.Zones.Count == 0
        ? Loc.T("нет данных о зонах", "no zone data")
        : string.Join(" → ", route.Zones.Select(z => $"{z.Zone} {Texts.Time(z.Arrival)}"));

    /// <summary>Сколько зон маршрута показывать в карточке раунда; остальные — «ещё N».</summary>
    private const int VisibleSteps = 6;

    public string Title => $"{Loc.T("Раунд", "Round")} {route.Round}";

    public string SideText => Texts.SideShort(route.Side);

    public bool IsT => route.Side == Side.T;

    public bool Died => route.Died;

    public string FateText => route.Died ? Loc.T("погиб", "died") : Loc.T("выжил", "survived");

    public IReadOnlyList<RouteStep> Steps { get; } = route.Zones.Take(VisibleSteps)
        .Select((z, i) => new RouteStep(z.Zone, Texts.Time(z.Arrival), i < Math.Min(VisibleSteps, route.Zones.Count) - 1 || route.Zones.Count > VisibleSteps))
        .ToList();

    public bool HasMoreSteps => route.Zones.Count > VisibleSteps;

    public string MoreStepsText => Loc.T($"ещё {route.Zones.Count - VisibleSteps}", $"{route.Zones.Count - VisibleSteps} more");

    public bool HasNoZones => route.Zones.Count == 0;
}

/// <summary>Зона маршрута в карточке раунда: «BHalls 0:07 ›».</summary>
public sealed record RouteStep(string Zone, string Time, bool HasNext);

public sealed class FrequentRouteRow(FrequentRoute route)
{
    public FrequentRoute Route { get; } = route;

    public string Header => $"{Texts.SideShort(Route.Side)} · {Route.Rounds.Count} {Loc.T("раунд(ов)", "round(s)")} · {Route.Share:P0}";

    public string ZonesText => string.Join(" → ", Route.Zones);

    public string RoundsText => Loc.T("Раунды: ", "Rounds: ") + string.Join(", ", Route.Rounds);
}

public sealed class ZoneTimingRow(ZoneTiming timing)
{
    public string Side => Texts.SideShort(timing.Side);

    public string Zone => timing.Zone;

    public int Rounds => timing.Rounds;

    public string Median => Texts.Time(timing.MedianArrival);

    public string Range => $"{Texts.Time(timing.EarliestArrival)} – {Texts.Time(timing.LatestArrival)}";
}

public sealed class GrenadeClusterRow(GrenadeCluster cluster, IReadOnlyDictionary<int, GrenadeDamage> damage)
{
    public GrenadeCluster Cluster { get; } = cluster;

    /// <summary>Бросок, ближе всех к средней точке раскидки, — его сохраняют в библиотеку и копируют setpos.</summary>
    public GrenadeThrow Representative { get; } = cluster.Throws.MinBy(t => Vector3.DistanceSquared(t.ThrowerPosition, cluster.ThrowPoint))!;

    public string Header =>
        $"{Texts.GrenadeName(Cluster.Type)} ×{Cluster.Throws.Count} · {Texts.SideShort(Cluster.Side)} · ~{Texts.Time(Cluster.MedianThrowTime)}";

    public string RouteText
    {
        get
        {
            var text = $"{Texts.ZoneOrUnknown(Cluster.FromZone)} → {Texts.ZoneOrUnknown(Cluster.ToZone)}";
            var total = Cluster.Throws.Sum(t => damage.GetValueOrDefault(t.Id)?.EnemyDamage ?? 0);
            return total > 0 ? text + Loc.T($" · урон {total}", $" · damage {total}") : text;
        }
    }

    public string RoundsText => Loc.T("Раунды: ", "Rounds: ") + string.Join(", ", Cluster.Rounds);
}

public sealed class GrenadeThrowRow(GrenadeThrow grenade, string landZone, GrenadeDamage? damage, MatchData match)
{
    public GrenadeThrow Throw { get; } = grenade;

    public string Header =>
        $"{Loc.T("Р", "R")}{Throw.Round} · {Texts.Time(Throw.ThrowTime)} · {Texts.GrenadeName(Throw.Type)} · {Texts.SideShort(Throw.ThrowerSide)}";

    public string Details
    {
        get
        {
            var target = Throw.Detonated ? Texts.ZoneOrUnknown(landZone) : Loc.T("не сработала", "did not go off");
            var text = $"{Texts.ZoneOrUnknown(Throw.ThrowerPlace)} → {target}";
            if (damage is not null && (damage.EnemyDamage > 0 || damage.TeamDamage > 0))
            {
                text += Loc.T($" · урон {damage.EnemyDamage} (противников: {damage.EnemiesHit})",
                    $" · damage {damage.EnemyDamage} (enemies hit: {damage.EnemiesHit})");
                if (damage.TeamDamage > 0)
                    text += Loc.T($", своим {damage.TeamDamage}", $", teammates {damage.TeamDamage}");
            }
            if (Throw.Type != GrenadeType.Flashbang || Throw.FlashVictims.Count == 0)
                return text;

            var victims = Throw.FlashVictims
                .OrderBy(v => v.IsTeammate)
                .Select(v => (v.IsSelf ? Loc.T("себя", "self") : match.FindPlayer(v.SteamId)?.Name ?? "?") +
                             (v.IsTeammate && !v.IsSelf ? Loc.T(" (союзник)", " (teammate)") : "") +
                             Loc.T($" {v.Duration:F1} с", $" {v.Duration:F1} s"));
            return text + Loc.T(" · ослепил: ", " · blinded: ") + string.Join(", ", victims);
        }
    }
}

public sealed class GrenadeSummaryRow(GrenadeTypeSummary summary)
{
    public string Text => Loc.T(
        $"{Texts.SideShort(summary.Side)} · {Texts.GrenadeName(summary.Type)}: {summary.Count} в {summary.Rounds} раунд(ах), медиана {Texts.Time(summary.MedianThrowTime)}",
        $"{Texts.SideShort(summary.Side)} · {Texts.GrenadeName(summary.Type)}: {summary.Count} in {summary.Rounds} round(s), median {Texts.Time(summary.MedianThrowTime)}");
}
