using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace DemReview.Desktop.Controls;

/// <summary>Маршрут для отрисовки: точки в пикселях радара (0..1024).</summary>
/// <param name="LowerLevel">Для многоуровневых карт: точка на нижнем уровне (рисуется пунктиром).</param>
public sealed record RadarRoute(int Round, IReadOnlyList<Point> Points, IReadOnlyList<bool> LowerLevel, Color Color, bool Died);

/// <summary>
/// Радар карты: подложка (изображение или сетка), слои данных, зум колесом мыши и панорамирование.
/// Всё рисуется в одном <see cref="Render"/> — без отдельных контролов на каждую точку.
/// </summary>
public sealed class RadarControl : Control
{
    private const double RadarSize = 1024;
    private const double MinZoom = 0.5;
    private const double MaxZoom = 12;

    public static readonly StyledProperty<Bitmap?> MapImageProperty =
        AvaloniaProperty.Register<RadarControl, Bitmap?>(nameof(MapImage));

    public static readonly StyledProperty<IReadOnlyList<RadarRoute>?> RoutesProperty =
        AvaloniaProperty.Register<RadarControl, IReadOnlyList<RadarRoute>?>(nameof(Routes));

    /// <summary>Выделенные раунды; остальные маршруты приглушаются. Пусто — все одинаково яркие.</summary>
    public static readonly StyledProperty<IReadOnlySet<int>?> HighlightedRoundsProperty =
        AvaloniaProperty.Register<RadarControl, IReadOnlySet<int>?>(nameof(HighlightedRounds));

    public static readonly StyledProperty<bool> ShowRoutesProperty =
        AvaloniaProperty.Register<RadarControl, bool>(nameof(ShowRoutes), true);

    /// <summary>Функция перевода пикселей радара в игровые координаты — для подписи под курсором.</summary>
    public static readonly StyledProperty<Func<Point, Point>?> RadarToWorldProperty =
        AvaloniaProperty.Register<RadarControl, Func<Point, Point>?>(nameof(RadarToWorld));

    private double _zoom = 1;
    private Vector _pan;
    private Point? _dragStart;
    private Vector _panAtDragStart;
    private Point? _cursorRadar;
    private Dictionary<int, StreamGeometry>? _geometryCache;

    static RadarControl()
    {
        AffectsRender<RadarControl>(MapImageProperty, RoutesProperty, HighlightedRoundsProperty, ShowRoutesProperty);
        RoutesProperty.Changed.AddClassHandler<RadarControl>((c, _) => c._geometryCache = null);
        ClipToBoundsProperty.OverrideDefaultValue<RadarControl>(true);
        FocusableProperty.OverrideDefaultValue<RadarControl>(true);
    }

    public Bitmap? MapImage
    {
        get => GetValue(MapImageProperty);
        set => SetValue(MapImageProperty, value);
    }

    public IReadOnlyList<RadarRoute>? Routes
    {
        get => GetValue(RoutesProperty);
        set => SetValue(RoutesProperty, value);
    }

    public IReadOnlySet<int>? HighlightedRounds
    {
        get => GetValue(HighlightedRoundsProperty);
        set => SetValue(HighlightedRoundsProperty, value);
    }

    public bool ShowRoutes
    {
        get => GetValue(ShowRoutesProperty);
        set => SetValue(ShowRoutesProperty, value);
    }

    public Func<Point, Point>? RadarToWorld
    {
        get => GetValue(RadarToWorldProperty);
        set => SetValue(RadarToWorldProperty, value);
    }

    /// <summary>Сброс зума и сдвига (двойной клик).</summary>
    public void ResetView()
    {
        _zoom = 1;
        _pan = default;
        InvalidateVisual();
    }

    // ---------- Преобразование координат ----------

    /// <summary>Масштаб «пиксель радара → пиксель экрана» с учётом зума.</summary>
    private double Scale => Math.Min(Bounds.Width, Bounds.Height) / RadarSize * _zoom;

    /// <summary>Положение левого верхнего угла радара на экране.</summary>
    private Point Origin => new(
        (Bounds.Width - RadarSize * Scale) / 2 + _pan.X,
        (Bounds.Height - RadarSize * Scale) / 2 + _pan.Y);

    private Matrix RadarToScreen => Matrix.CreateScale(Scale, Scale) * Matrix.CreateTranslation(Origin.X, Origin.Y);

    private Point ScreenToRadar(Point screen) => new((screen.X - Origin.X) / Scale, (screen.Y - Origin.Y) / Scale);

    // ---------- Отрисовка ----------

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.Black, new Rect(Bounds.Size));
        if (Bounds.Width < 1 || Bounds.Height < 1)
            return;

        var scale = Scale;
        using (context.PushTransform(RadarToScreen))
        {
            DrawBackground(context, scale);
            if (ShowRoutes)
                DrawRoutes(context, scale);
        }

        DrawCursorCoordinates(context);
    }

    private void DrawBackground(DrawingContext context, double scale)
    {
        var radarRect = new Rect(0, 0, RadarSize, RadarSize);
        if (MapImage is { } image)
        {
            context.DrawImage(image, new Rect(image.Size), radarRect);
            return;
        }

        // Нет изображения карты — сетка с шагом 64 px радара.
        context.FillRectangle(new SolidColorBrush(Color.FromRgb(18, 22, 28)), radarRect);
        var minor = new Pen(new SolidColorBrush(Color.FromArgb(40, 160, 170, 190)), 1 / scale);
        var major = new Pen(new SolidColorBrush(Color.FromArgb(90, 160, 170, 190)), 1 / scale);
        for (var i = 0; i <= RadarSize; i += 64)
        {
            var pen = i % 256 == 0 ? major : minor;
            context.DrawLine(pen, new Point(i, 0), new Point(i, RadarSize));
            context.DrawLine(pen, new Point(0, i), new Point(RadarSize, i));
        }
    }

    private void DrawRoutes(DrawingContext context, double scale)
    {
        if (Routes is not { Count: > 0 } routes)
            return;

        _geometryCache ??= routes.ToDictionary(r => r.Round, BuildGeometry);
        var highlighted = HighlightedRounds is { Count: > 0 } set ? set : null;

        // Сначала приглушённые, поверх — выделенные.
        foreach (var route in routes.OrderBy(r => highlighted?.Contains(r.Round) == true))
        {
            var isActive = highlighted is null || highlighted.Contains(route.Round);
            var alpha = (byte)(isActive ? 230 : 45);
            var color = Color.FromArgb(alpha, route.Color.R, route.Color.G, route.Color.B);
            var thickness = (isActive && highlighted is not null ? 3 : 2) / scale;
            var pen = new Pen(new SolidColorBrush(color), thickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);

            context.DrawGeometry(null, pen, _geometryCache[route.Round]);
            DrawLowerLevelMarks(context, route, color, scale);

            if (route.Points.Count == 0)
                continue;

            // Начало маршрута — кружок, конец — крест при смерти или квадрат, если игрок выжил.
            var brush = new SolidColorBrush(color);
            context.DrawEllipse(brush, null, route.Points[0], 3.5 / scale, 3.5 / scale);
            var end = route.Points[^1];
            if (route.Died)
            {
                var d = 5 / scale;
                var deathPen = new Pen(brush, 2.2 / scale);
                context.DrawLine(deathPen, end + new Vector(-d, -d), end + new Vector(d, d));
                context.DrawLine(deathPen, end + new Vector(-d, d), end + new Vector(d, -d));
            }
            else
            {
                var s = 3.5 / scale;
                context.DrawRectangle(brush, null, new Rect(end.X - s, end.Y - s, 2 * s, 2 * s));
            }
        }
    }

    /// <summary>Точки нижнего уровня (nuke) отмечаются полыми кружками поверх линии.</summary>
    private static void DrawLowerLevelMarks(DrawingContext context, RadarRoute route, Color color, double scale)
    {
        if (!route.LowerLevel.Contains(true))
            return;

        var pen = new Pen(new SolidColorBrush(color), 1 / scale);
        for (var i = 0; i < route.Points.Count; i += 4)
        {
            if (route.LowerLevel[i])
                context.DrawEllipse(null, pen, route.Points[i], 2.5 / scale, 2.5 / scale);
        }
    }

    private static StreamGeometry BuildGeometry(RadarRoute route)
    {
        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();
        if (route.Points.Count > 0)
        {
            ctx.BeginFigure(route.Points[0], isFilled: false);
            for (var i = 1; i < route.Points.Count; i++)
                ctx.LineTo(route.Points[i]);
            ctx.EndFigure(isClosed: false);
        }
        return geometry;
    }

    private void DrawCursorCoordinates(DrawingContext context)
    {
        if (_cursorRadar is not { } radar || radar.X < 0 || radar.Y < 0 || radar.X > RadarSize || radar.Y > RadarSize)
            return;

        var text = RadarToWorld is { } toWorld
            ? $"X {toWorld(radar).X:F0}  Y {toWorld(radar).Y:F0}"
            : $"{radar.X:F0}, {radar.Y:F0}";
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            Typeface.Default, 12, Brushes.White);
        var rect = new Rect(8, Bounds.Height - formatted.Height - 12, formatted.Width + 12, formatted.Height + 6);
        context.FillRectangle(new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)), rect);
        context.DrawText(formatted, rect.TopLeft + new Vector(6, 3));
    }

    // ---------- Мышь: зум колесом вокруг курсора, перетаскивание, двойной клик — сброс ----------

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var cursor = e.GetPosition(this);
        var radarBefore = ScreenToRadar(cursor);

        _zoom = Math.Clamp(_zoom * Math.Pow(1.2, e.Delta.Y), MinZoom, MaxZoom);

        // Точка под курсором остаётся на месте.
        var radarAfter = ScreenToRadar(cursor);
        _pan += new Vector((radarAfter.X - radarBefore.X) * Scale, (radarAfter.Y - radarBefore.Y) * Scale);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.ClickCount == 2)
        {
            ResetView();
            e.Handled = true;
            return;
        }

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || e.GetCurrentPoint(this).Properties.IsMiddleButtonPressed)
        {
            _dragStart = e.GetPosition(this);
            _panAtDragStart = _pan;
            e.Pointer.Capture(this);
            Cursor = new Cursor(StandardCursorType.SizeAll);
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var position = e.GetPosition(this);
        if (_dragStart is { } start)
            _pan = _panAtDragStart + (position - start);

        _cursorRadar = ScreenToRadar(position);
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragStart = null;
        e.Pointer.Capture(null);
        Cursor = Cursor.Default;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _cursorRadar = null;
        InvalidateVisual();
    }
}
