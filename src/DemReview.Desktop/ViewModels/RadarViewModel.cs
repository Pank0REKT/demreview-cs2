using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemReview.Application;
using DemReview.Application.Grenades;
using DemReview.Application.Maps;
using DemReview.Application.Routes;
using DemReview.Desktop.Controls;
using DemReview.Domain;
using Microsoft.Extensions.Logging;

namespace DemReview.Desktop.ViewModels;

/// <summary>Вкладка «Радар»: подложка карты, маршруты и гранаты выбранного игрока, частые маршруты, раскидки.</summary>
public partial class RadarViewModel(
    IRouteAnalysisService routeAnalysis,
    IGrenadeAnalysisService grenadeAnalysis,
    IMapConfigProvider mapConfigs,
    IMapImageProvider mapImages,
    IMapCoordinateConverter converter,
    ISettingsService settings,
    IPlatformService platform,
    ILogger<RadarViewModel> logger) : ViewModelBase
{
    /// <summary>Радиус облака дыма и зоны огня, игровые units.</summary>
    private const float SmokeRadius = 144;
    private const float FireRadius = 120;

    private MatchData? _match;
    private MapConfig? _map;
    private RouteAnalysis? _routes;
    private GrenadeAnalysis? _grenades;
    private int _analysisVersion;
    private bool _updatingSelection;

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
    public partial bool ShowT { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowCT { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowHe { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowFlash { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowSmoke { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowFire { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowDecoy { get; set; } = true;

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

    public ObservableCollection<RouteRoundRow> RoundRows { get; } = [];

    public ObservableCollection<FrequentRouteRow> FrequentRoutes { get; } = [];

    public ObservableCollection<ZoneTimingRow> ZoneTimings { get; } = [];

    public ObservableCollection<GrenadeClusterRow> GrenadeClusters { get; } = [];

    public ObservableCollection<GrenadeThrowRow> GrenadeThrows { get; } = [];

    public ObservableCollection<GrenadeSummaryRow> GrenadeSummary { get; } = [];

    public bool HasMapHint => MapHint is not null;

    partial void OnShowTChanged(bool value) => RebuildLayers();
    partial void OnShowCTChanged(bool value) => RebuildLayers();
    partial void OnShowHeChanged(bool value) => RebuildLayers();
    partial void OnShowFlashChanged(bool value) => RebuildLayers();
    partial void OnShowSmokeChanged(bool value) => RebuildLayers();
    partial void OnShowFireChanged(bool value) => RebuildLayers();
    partial void OnShowDecoyChanged(bool value) => RebuildLayers();

    partial void OnSelectedRoundChanged(RouteRoundRow? value) => OnSelectionChanged(value);
    partial void OnSelectedFrequentRouteChanged(FrequentRouteRow? value) => OnSelectionChanged(value);
    partial void OnSelectedClusterChanged(GrenadeClusterRow? value) => OnSelectionChanged(value);
    partial void OnSelectedThrowChanged(GrenadeThrowRow? value) => OnSelectionChanged(value);

    /// <summary>Новая демка: конфиг карты и изображение подложки.</summary>
    public void SetMatch(MatchData match)
    {
        _match = match;
        _map = mapConfigs.Find(match.MapName);
        var hints = new List<string>();

        if (_map is null)
        {
            _map = converter.FitToPositions(match.MapName,
                match.Tracks.Values.SelectMany(t => t.Samples).Select(s => s.Position));
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
            var world = converter.ToWorld(map, new System.Numerics.Vector2((float)p.X, (float)p.Y));
            return new Point(world.X, world.Y);
        };
        MapTitle = map.DisplayName;
        MapHint = hints.Count > 0 ? string.Join(Environment.NewLine, hints) : null;
        Clear();
    }

    /// <summary>Анализ маршрутов и гранат выбранного игрока — в фоновом потоке.</summary>
    public async Task ShowPlayerAsync(ulong? steamId)
    {
        var version = ++_analysisVersion;
        if (_match is not { } match || steamId is not { } id)
        {
            Clear();
            return;
        }

        var current = settings.Current;
        var clusterOptions = new GrenadeClusterOptions(current.GrenadeClusterThrowDistance, current.GrenadeClusterLandDistance);
        var (routes, grenades) = await Task.Run(() =>
            (routeAnalysis.Analyze(match, id), grenadeAnalysis.Analyze(match, id, clusterOptions)));
        if (version != _analysisVersion)
            return;

        _routes = routes;
        _grenades = grenades;
        FillLists(routes, grenades);
        RebuildLayers();
    }

    [RelayCommand]
    private void OpenMapsFolder() => platform.OpenFolder(platform.MapsDirectory);

    [RelayCommand]
    private void ClearSelection() => OnSelectionChanged(null);

    private Bitmap? LoadImage(string mapName, List<string> hints)
    {
        if (mapImages.FindImage(mapName) is not { } path)
        {
            hints.Add($"Нет изображения карты. Положите {Path.GetFileName(mapImages.ExpectedPath(mapName))} в папку {platform.MapsDirectory}");
            return null;
        }

        try
        {
            return new Bitmap(path);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Не удалось загрузить изображение карты {Path}", path);
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
        OnSelectionChanged(null);
    }

    private void FillLists(RouteAnalysis routes, GrenadeAnalysis grenades)
    {
        OnSelectionChanged(null);

        RoundRows.Clear();
        foreach (var round in routes.Rounds)
            RoundRows.Add(new RouteRoundRow(round));

        FrequentRoutes.Clear();
        foreach (var route in routes.FrequentRoutes)
            FrequentRoutes.Add(new FrequentRouteRow(route));

        ZoneTimings.Clear();
        foreach (var timing in routes.ZoneTimings.Where(t => t.Rounds >= 2))
            ZoneTimings.Add(new ZoneTimingRow(timing));

        GrenadeClusters.Clear();
        foreach (var cluster in grenades.Clusters)
            GrenadeClusters.Add(new GrenadeClusterRow(cluster));

        GrenadeThrows.Clear();
        foreach (var t in grenades.Throws)
            GrenadeThrows.Add(new GrenadeThrowRow(t, grenades.LandZones.GetValueOrDefault(t.Id, ""), _match!));

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

    private bool IsSideVisible(Side side) => side switch
    {
        Side.T => ShowT,
        Side.CT => ShowCT,
        _ => ShowT && ShowCT,
    };

    private bool IsTypeVisible(GrenadeType type) => type switch
    {
        GrenadeType.HighExplosive => ShowHe,
        GrenadeType.Flashbang => ShowFlash,
        GrenadeType.Smoke => ShowSmoke,
        GrenadeType.Molotov or GrenadeType.Incendiary => ShowFire,
        GrenadeType.Decoy => ShowDecoy,
        _ => true,
    };

    private void RebuildLayers()
    {
        if (_map is not { } map)
            return;

        Point ToRadar(System.Numerics.Vector3 world)
        {
            var v = converter.ToRadar(map, world);
            return new Point(v.X, v.Y);
        }

        var tIndex = 0;
        var ctIndex = 0;
        Routes = _routes is null ? [] : _routes.Rounds
            .Where(r => IsSideVisible(r.Side))
            .Select(r =>
            {
                var color = r.Side == Side.CT ? Palette.Ct(ctIndex++) : Palette.T(tIndex++);
                var points = r.Path.Select(s => ToRadar(s.Position)).ToList();
                var lower = r.Path.Select(s => converter.IsLowerLevel(map, s.Position)).ToList();
                return new RadarRoute(r.Round, points, lower, color, r.Died);
            })
            .ToList();

        Grenades = _grenades is null ? [] : _grenades.Throws
            .Where(g => IsSideVisible(g.ThrowerSide) && IsTypeVisible(g.Type))
            .Select(g => new RadarGrenade(
                g.Id,
                g.Round,
                KindOf(g.Type),
                ToRadar(g.ThrowerPosition),
                g.DetonatePosition is { } land ? ToRadar(land) : null,
                (g.Type == GrenadeType.Smoke ? SmokeRadius : FireRadius) / map.Scale,
                Palette.Grenade(g.Type)))
            .ToList();
    }

    /// <summary>Выбор в одном списке снимает выбор в остальных и задаёт подсветку на радаре.</summary>
    private void OnSelectionChanged(object? selected)
    {
        if (_updatingSelection)
            return;

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
