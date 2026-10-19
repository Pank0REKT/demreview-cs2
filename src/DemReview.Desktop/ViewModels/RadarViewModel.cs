using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemReview.Application;
using DemReview.Application.Maps;
using DemReview.Application.Routes;
using DemReview.Desktop.Controls;
using DemReview.Domain;
using Microsoft.Extensions.Logging;

namespace DemReview.Desktop.ViewModels;

/// <summary>Вкладка «Радар»: подложка карты, маршруты выбранного игрока, частые маршруты и тайминги.</summary>
public partial class RadarViewModel(
    IRouteAnalysisService routeAnalysis,
    IMapConfigProvider mapConfigs,
    IMapImageProvider mapImages,
    IMapCoordinateConverter converter,
    IPlatformService platform,
    ILogger<RadarViewModel> logger) : ViewModelBase
{
    private MatchData? _match;
    private MapConfig? _map;
    private RouteAnalysis? _analysis;
    private int _analysisVersion;

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
    public partial IReadOnlySet<int> HighlightedRounds { get; private set; } = new HashSet<int>();

    [ObservableProperty]
    public partial Func<Point, Point>? RadarToWorld { get; private set; }

    [ObservableProperty]
    public partial bool ShowRoutes { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowT { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowCT { get; set; } = true;

    [ObservableProperty]
    public partial RouteRoundRow? SelectedRound { get; set; }

    [ObservableProperty]
    public partial FrequentRouteRow? SelectedFrequentRoute { get; set; }

    [ObservableProperty]
    public partial bool IsAnalyzing { get; private set; }

    public ObservableCollection<RouteRoundRow> RoundRows { get; } = [];

    public ObservableCollection<FrequentRouteRow> FrequentRoutes { get; } = [];

    public ObservableCollection<ZoneTimingRow> ZoneTimings { get; } = [];

    public bool HasMapHint => MapHint is not null;

    partial void OnShowTChanged(bool value) => RebuildRoutes();

    partial void OnShowCTChanged(bool value) => RebuildRoutes();

    partial void OnSelectedRoundChanged(RouteRoundRow? value)
    {
        if (value is not null)
            SelectedFrequentRoute = null;
        UpdateHighlight();
    }

    partial void OnSelectedFrequentRouteChanged(FrequentRouteRow? value)
    {
        if (value is not null)
            SelectedRound = null;
        UpdateHighlight();
    }

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

    /// <summary>Анализ маршрутов выбранного игрока — в фоновом потоке.</summary>
    public async Task ShowPlayerAsync(ulong? steamId)
    {
        var version = ++_analysisVersion;
        if (_match is not { } match || steamId is not { } id)
        {
            Clear();
            return;
        }

        IsAnalyzing = true;
        try
        {
            var analysis = await Task.Run(() => routeAnalysis.Analyze(match, id));
            if (version != _analysisVersion)
                return;

            _analysis = analysis;
            FillLists(analysis);
            RebuildRoutes();
        }
        finally
        {
            if (version == _analysisVersion)
                IsAnalyzing = false;
        }
    }

    [RelayCommand]
    private void OpenMapsFolder() => platform.OpenFolder(platform.MapsDirectory);

    [RelayCommand]
    private void ClearSelection()
    {
        SelectedRound = null;
        SelectedFrequentRoute = null;
    }

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
        _analysis = null;
        RoundRows.Clear();
        FrequentRoutes.Clear();
        ZoneTimings.Clear();
        Routes = [];
        HighlightedRounds = new HashSet<int>();
    }

    private void FillLists(RouteAnalysis analysis)
    {
        SelectedRound = null;
        SelectedFrequentRoute = null;

        RoundRows.Clear();
        foreach (var round in analysis.Rounds)
            RoundRows.Add(new RouteRoundRow(round));

        FrequentRoutes.Clear();
        foreach (var route in analysis.FrequentRoutes)
            FrequentRoutes.Add(new FrequentRouteRow(route));

        ZoneTimings.Clear();
        foreach (var timing in analysis.ZoneTimings.Where(t => t.Rounds >= 2))
            ZoneTimings.Add(new ZoneTimingRow(timing));
    }

    private void RebuildRoutes()
    {
        if (_analysis is not { } analysis || _map is not { } map)
        {
            Routes = [];
            return;
        }

        var tIndex = 0;
        var ctIndex = 0;
        Routes = analysis.Rounds
            .Where(r => r.Side == Side.T ? ShowT : r.Side == Side.CT ? ShowCT : ShowT && ShowCT)
            .Select(r =>
            {
                var color = r.Side == Side.CT ? Palette.Ct(ctIndex++) : Palette.T(tIndex++);
                var points = r.Path.Select(s => converter.ToRadar(map, s.Position)).Select(v => new Point(v.X, v.Y)).ToList();
                var lower = r.Path.Select(s => converter.IsLowerLevel(map, s.Position)).ToList();
                return new RadarRoute(r.Round, points, lower, color, r.Died);
            })
            .ToList();
    }

    private void UpdateHighlight()
    {
        HighlightedRounds = SelectedRound is { } round ? new HashSet<int> { round.Round }
            : SelectedFrequentRoute is { } frequent ? frequent.Route.Rounds.ToHashSet()
            : new HashSet<int>();
    }

    /// <summary>Оттенки для маршрутов: тёплые для T, холодные для CT, соседние раунды различимы.</summary>
    private static class Palette
    {
        public static Color T(int i) => FromHsv(18 + (i * 37) % 50, 0.85, 1.0);

        public static Color Ct(int i) => FromHsv(185 + (i * 41) % 60, 0.75, 1.0);

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
