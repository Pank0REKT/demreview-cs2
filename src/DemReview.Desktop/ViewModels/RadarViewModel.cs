using System.Collections.ObjectModel;
using System.Numerics;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemReview.Application;
using DemReview.Application.Context;
using DemReview.Application.Filters;
using DemReview.Application.Grenades;
using DemReview.Application.Heatmaps;
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
/// с учётом фильтров, частые маршруты, раскидки и таймлайн раунда с воспроизведением.
/// </summary>
public partial class RadarViewModel : ViewModelBase
{
    /// <summary>Радиус облака дыма и зоны огня, игровые units; время действия, секунды.</summary>
    private const float SmokeRadius = 144;
    private const float FireRadius = 120;
    private const float SmokeSeconds = 18;
    private const float FireSeconds = 7;

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
    private AnalysisFilter _filter = AnalysisFilter.None;
    private int _analysisVersion;
    private int _heatmapVersion;
    private bool _updatingSelection;
    private IReadOnlySet<int>? _focusRounds;

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
        new("позиции", HeatmapMode.Positions),
        new("смерти", HeatmapMode.Deaths),
    ];

    public ObservableCollection<RouteRoundRow> RoundRows { get; } = [];

    public ObservableCollection<FrequentRouteRow> FrequentRoutes { get; } = [];

    public ObservableCollection<ZoneTimingRow> ZoneTimings { get; } = [];

    public ObservableCollection<GrenadeClusterRow> GrenadeClusters { get; } = [];

    public ObservableCollection<GrenadeThrowRow> GrenadeThrows { get; } = [];

    public ObservableCollection<GrenadeSummaryRow> GrenadeSummary { get; } = [];

    public bool HasMapHint => MapHint is not null;

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
        _map = _mapConfigs.Find(match.MapName);
        var hints = new List<string>();

        if (_map is null)
        {
            _map = _converter.FitToPositions(match.MapName,
                match.Tracks.Values.SelectMany(t => t.Samples).Select(sample => sample.Position));
            hints.Add($"Карта {match.MapName} не описана в maps.json — масштаб подобран по позициям игроков, подложки нет.");
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
        FocusText = $"Раунды-примеры: {string.Join(", ", pattern.Rounds)} — {pattern.Text}";
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
            hints.Add($"Нет изображения карты. Положите {Path.GetFileName(_mapImages.ExpectedPath(mapName))} в папку {_platform.MapsDirectory}");
            return null;
        }

        try
        {
            return new Bitmap(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось загрузить изображение карты {Path}", path);
            hints.Add($"Не удалось загрузить {path}: {ex.Message}");
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
            GrenadeClusters.Add(new GrenadeClusterRow(cluster));

        GrenadeThrows.Clear();
        foreach (var t in grenades.Throws.Where(IncludesGrenade))
            GrenadeThrows.Add(new GrenadeThrowRow(t, grenades.LandZones.GetValueOrDefault(t.Id, ""), match));

        GrenadeSummary.Clear();
        foreach (var s in grenades.Summary)
            GrenadeSummary.Add(new GrenadeSummaryRow(s));

        var f = grenades.Flashes;
        FlashSummaryText = f.Thrown == 0
            ? "Флешек не бросал."
            : $"Флешек: {f.Thrown}. Ослепил противников: {f.EnemiesBlinded} ({f.EnemyBlindSeconds:F1} с), " +
              $"союзников: {f.TeammatesBlinded} ({f.TeammateBlindSeconds:F1} с), себя: {f.SelfBlinded}. " +
              $"Без ослеплённых противников: {f.WithoutEnemyVictims}.";
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

        Grenades = _grenades is null ? [] : _grenades.Throws
            .Where(g => RoundVisible(g.Round) && _filter.IncludesGrenade(g) && g.ThrowTick <= tick)
            .Select(g => new RadarGrenade(
                g.Id,
                g.Round,
                KindOf(g.Type),
                ToRadar(g.ThrowerPosition),
                g.DetonatePosition is { } land && g.DetonateTick <= tick && !EffectExpired(g, tick) ? ToRadar(land) : null,
                (g.Type == GrenadeType.Smoke ? SmokeRadius : FireRadius) / map.Scale,
                Palette.Grenade(g.Type)))
            .ToList();

        Deaths = !ShowDeaths ? [] : match.Kills
            .Where(k => k.VictimSteamId == steamId && RoundVisible(k.Round) && k.Tick <= tick)
            .Select(k => ToRadar(k.VictimPosition))
            .ToList();

        if (timelineRound is null)
        {
            Players = null;
            Timeline.ContextText = "";
        }
        else
        {
            Players = BuildMarkers(match, timelineRound.Number, tick, steamId);
            var moment = _context!.At(steamId, timelineRound.Number, tick);
            Timeline.ContextText = MomentText(moment);
        }
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

    /// <summary>Маркеры живых игроков с интерполяцией между сэмплами — плавное движение при воспроизведении.</summary>
    private List<RadarPlayerMarker> BuildMarkers(MatchData match, int round, int tick, ulong selected)
    {
        var markers = new List<RadarPlayerMarker>();
        foreach (var player in match.Players)
        {
            var side = player.SideInRound(round);
            if (side == Side.None || !_context!.IsAlive(player.SteamId, round, tick))
                continue;

            var samples = match.TrackOf(player.SteamId).Samples;
            var index = LastAtOrBefore(samples, tick);
            if (index < 0 || samples[index].Round != round)
                continue;

            var a = samples[index];
            var (position, yaw) = (a.Position, a.Yaw);
            if (index + 1 < samples.Count && samples[index + 1] is { } b && b.Round == round && b.Tick > a.Tick)
            {
                var t = Math.Clamp((tick - a.Tick) / (float)(b.Tick - a.Tick), 0, 1);
                position = Vector3.Lerp(a.Position, b.Position, t);
                yaw = LerpAngle(a.Yaw, b.Yaw, t);
            }

            var isSelected = player.SteamId == selected;
            var color = isSelected ? Color.FromRgb(255, 255, 255)
                : side == Side.CT ? Color.FromRgb(90, 170, 255) : Color.FromRgb(255, 170, 60);
            markers.Add(new RadarPlayerMarker(ToRadar(position), yaw, color, isSelected,
                isSelected ? $"{player.Name} · {a.Weapon} · {a.Health} HP" : ""));
        }
        return markers;
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

    private static string MomentText(MomentContext m)
    {
        var bomb = m.Bomb switch
        {
            BombState.Planted => $"бомба заложена на {m.BombSite}",
            BombState.Defused => "бомба обезврежена",
            BombState.Exploded => "бомба взорвалась",
            _ => "бомба не заложена",
        };
        return $"{Labels.Side(m.Side)} · {m.Matchup} · {(m.PlayerAlive ? $"${m.Money}, экип. ${m.Equipment}" : "погиб")} · " +
               $"закупка {Labels.Buy(m.TeamBuy)} против {Labels.Buy(m.EnemyBuy)} · {bomb} · счёт {m.ScoreUs}:{m.ScoreThem}";
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

public sealed class RouteRoundRow(RoundRoute route)
{
    public int Round => route.Round;

    public string Header => $"Раунд {route.Round} · {Texts.SideShort(route.Side)}{(route.Died ? " · погиб" : "")}";

    public string ZonesText => route.Zones.Count == 0
        ? "нет данных о зонах"
        : string.Join(" → ", route.Zones.Select(z => $"{z.Zone} {Texts.Time(z.Arrival)}"));
}

public sealed class FrequentRouteRow(FrequentRoute route)
{
    public FrequentRoute Route { get; } = route;

    public string Header => $"{Texts.SideShort(Route.Side)} · {Route.Rounds.Count} раунд(ов) · {Route.Share:P0}";

    public string ZonesText => string.Join(" → ", Route.Zones);

    public string RoundsText => "Раунды: " + string.Join(", ", Route.Rounds);
}

public sealed class ZoneTimingRow(ZoneTiming timing)
{
    public string Side => Texts.SideShort(timing.Side);

    public string Zone => timing.Zone;

    public int Rounds => timing.Rounds;

    public string Median => Texts.Time(timing.MedianArrival);

    public string Range => $"{Texts.Time(timing.EarliestArrival)} – {Texts.Time(timing.LatestArrival)}";
}

public sealed class GrenadeClusterRow(GrenadeCluster cluster)
{
    public GrenadeCluster Cluster { get; } = cluster;

    public string Header =>
        $"{Texts.GrenadeName(Cluster.Type)} ×{Cluster.Throws.Count} · {Texts.SideShort(Cluster.Side)} · ~{Texts.Time(Cluster.MedianThrowTime)}";

    public string RouteText => $"{Texts.ZoneOrUnknown(Cluster.FromZone)} → {Texts.ZoneOrUnknown(Cluster.ToZone)}";

    public string RoundsText => "Раунды: " + string.Join(", ", Cluster.Rounds);
}

public sealed class GrenadeThrowRow(GrenadeThrow grenade, string landZone, MatchData match)
{
    public GrenadeThrow Throw { get; } = grenade;

    public string Header => $"Р{Throw.Round} · {Texts.Time(Throw.ThrowTime)} · {Texts.GrenadeName(Throw.Type)} · {Texts.SideShort(Throw.ThrowerSide)}";

    public string Details
    {
        get
        {
            var target = Throw.Detonated ? Texts.ZoneOrUnknown(landZone) : "не сработала";
            var text = $"{Texts.ZoneOrUnknown(Throw.ThrowerPlace)} → {target}";
            if (Throw.Type != GrenadeType.Flashbang || Throw.FlashVictims.Count == 0)
                return text;

            var victims = Throw.FlashVictims
                .OrderBy(v => v.IsTeammate)
                .Select(v => $"{(v.IsSelf ? "себя" : match.FindPlayer(v.SteamId)?.Name ?? "?")}{(v.IsTeammate && !v.IsSelf ? " (союзник)" : "")} {v.Duration:F1} с");
            return $"{text} · ослепил: {string.Join(", ", victims)}";
        }
    }
}

public sealed class GrenadeSummaryRow(GrenadeTypeSummary summary)
{
    public string Text =>
        $"{Texts.SideShort(summary.Side)} · {Texts.GrenadeName(summary.Type)}: {summary.Count} в {summary.Rounds} раунд(ах), медиана {Texts.Time(summary.MedianThrowTime)}";
}
