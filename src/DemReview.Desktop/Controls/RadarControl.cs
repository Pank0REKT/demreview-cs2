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

/// <summary>Игрок в текущий момент таймлайна.</summary>
/// <param name="Yaw">Угол обзора в градусах игры (0 — восток, 90 — север).</param>
/// <param name="IsSelected">Анализируемый игрок — рисуется крупнее, с направлением взгляда и подписью.</param>
/// <param name="Tooltip">Подсказка при наведении мышью (ник, оружие, здоровье).</param>
/// <param name="IsDead">Погиб в этом раунде — рисуется крестиком на месте смерти.</param>
public sealed record RadarPlayerMarker(
    Point Position, float Yaw, Color Color, bool IsSelected, string Label, string Name, string Tooltip, bool IsDead);

/// <summary>Всплывающий урон над жертвой: «−27».</summary>
/// <param name="Age">0 — только что, 1 — исчезает.</param>
public sealed record RadarDamageText(Point Position, string Text, Color Color, double Age);

/// <summary>Вид отметки детонации гранаты.</summary>
public enum RadarGrenadeKind { HighExplosive, Flash, Smoke, Fire, Decoy }

/// <summary>Граната для отрисовки: точка броска и детонации в пикселях радара.</summary>
/// <param name="EffectRadius">Радиус зоны действия (дым, огонь) в пикселях радара.</param>
/// <param name="DamageLabel">Урон гранаты (HE, огонь) — подпись у места взрыва.</param>
public sealed record RadarGrenade(
    int Id, int Round, RadarGrenadeKind Kind, Point From, Point? To, double EffectRadius, Color Color, string? DamageLabel = null);

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

    /// <summary>Фон вокруг карты — в тон карточки радара.</summary>
    private static readonly IBrush CanvasBrush = new SolidColorBrush(Color.FromRgb(13, 16, 21));

    private double _zoom = 1;
    private Vector _pan;
    private Point? _dragStart;
    private Vector _panAtDragStart;
    private Point? _cursorRadar;
    private Dictionary<int, StreamGeometry>? _geometryCache;

    static RadarControl()
    {
        AffectsRender<RadarControl>(MapImageProperty, RoutesProperty, HighlightedRoundsProperty, ShowRoutesProperty,
            GrenadesProperty, HighlightedGrenadesProperty, ShowGrenadesProperty, HeatmapProperty, DeathsProperty,
            PlayersProperty, DamageTextsProperty);
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

    public static readonly StyledProperty<IReadOnlyList<RadarGrenade>?> GrenadesProperty =
        AvaloniaProperty.Register<RadarControl, IReadOnlyList<RadarGrenade>?>(nameof(Grenades));

    /// <summary>Выделенные гранаты (кластер раскидки или один бросок).</summary>
    public static readonly StyledProperty<IReadOnlySet<int>?> HighlightedGrenadesProperty =
        AvaloniaProperty.Register<RadarControl, IReadOnlySet<int>?>(nameof(HighlightedGrenades));

    public static readonly StyledProperty<bool> ShowGrenadesProperty =
        AvaloniaProperty.Register<RadarControl, bool>(nameof(ShowGrenades), true);

    /// <summary>Тепловая карта (растягивается на весь радар 1024×1024).</summary>
    public static readonly StyledProperty<Bitmap?> HeatmapProperty =
        AvaloniaProperty.Register<RadarControl, Bitmap?>(nameof(Heatmap));

    /// <summary>Места смертей игрока, пиксели радара.</summary>
    public static readonly StyledProperty<IReadOnlyList<Point>?> DeathsProperty =
        AvaloniaProperty.Register<RadarControl, IReadOnlyList<Point>?>(nameof(Deaths));

    /// <summary>Игроки в текущий момент таймлайна (null — режим таймлайна выключен).</summary>
    public static readonly StyledProperty<IReadOnlyList<RadarPlayerMarker>?> PlayersProperty =
        AvaloniaProperty.Register<RadarControl, IReadOnlyList<RadarPlayerMarker>?>(nameof(Players));

    /// <summary>Всплывающий урон в момент таймлайна.</summary>
    public static readonly StyledProperty<IReadOnlyList<RadarDamageText>?> DamageTextsProperty =
        AvaloniaProperty.Register<RadarControl, IReadOnlyList<RadarDamageText>?>(nameof(DamageTexts));

    public IReadOnlyList<RadarDamageText>? DamageTexts
    {
        get => GetValue(DamageTextsProperty);
        set => SetValue(DamageTextsProperty, value);
    }

    public Bitmap? Heatmap
    {
        get => GetValue(HeatmapProperty);
        set => SetValue(HeatmapProperty, value);
    }

    public IReadOnlyList<Point>? Deaths
    {
        get => GetValue(DeathsProperty);
        set => SetValue(DeathsProperty, value);
    }

    public IReadOnlyList<RadarPlayerMarker>? Players
    {
        get => GetValue(PlayersProperty);
        set => SetValue(PlayersProperty, value);
    }

    public IReadOnlyList<RadarGrenade>? Grenades
    {
        get => GetValue(GrenadesProperty);
        set => SetValue(GrenadesProperty, value);
    }

    public IReadOnlySet<int>? HighlightedGrenades
    {
        get => GetValue(HighlightedGrenadesProperty);
        set => SetValue(HighlightedGrenadesProperty, value);
    }

    public bool ShowGrenades
    {
        get => GetValue(ShowGrenadesProperty);
        set => SetValue(ShowGrenadesProperty, value);
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

    /// <summary>Зум кнопками «+» и «−» вокруг центра радара.</summary>
    public void Zoom(double steps)
    {
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var radarBefore = ScreenToRadar(center);
        _zoom = Math.Clamp(_zoom * Math.Pow(1.2, steps), MinZoom, MaxZoom);
        var radarAfter = ScreenToRadar(center);
        _pan += new Vector((radarAfter.X - radarBefore.X) * Scale, (radarAfter.Y - radarBefore.Y) * Scale);
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
        context.FillRectangle(CanvasBrush, new Rect(Bounds.Size));
        if (Bounds.Width < 1 || Bounds.Height < 1)
            return;

        var scale = Scale;
        using (context.PushTransform(RadarToScreen))
        {
            DrawBackground(context, scale);
            if (Heatmap is { } heatmap)
            {
                using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
                    context.DrawImage(heatmap, new Rect(heatmap.Size), new Rect(0, 0, RadarSize, RadarSize));
            }
            if (ShowRoutes)
                DrawRoutes(context, scale);
            if (ShowGrenades)
                DrawGrenades(context, scale);
            DrawDeaths(context, scale);
            DrawPlayers(context, scale);
        }

        // Подписи — в экранных координатах: размер шрифта не зависит от зума.
        if (ShowGrenades)
            DrawGrenadeDamage(context);
        DrawDamageTexts(context);
        DrawHoveredPlayer(context);
        DrawCursorCoordinates(context);
    }

    private Point ToScreen(Point radar) => new(radar.X * Scale + Origin.X, radar.Y * Scale + Origin.Y);

    private static FormattedText Text(string text, double size, IBrush brush, FontWeight weight = FontWeight.Normal) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default, FontStyle.Normal, weight), size, brush);

    /// <summary>Текст с тёмной обводкой — читается на любой подложке.</summary>
    private static void DrawOutlined(DrawingContext context, FormattedText text, Point origin, double opacity = 1)
    {
        using (context.PushOpacity(opacity))
        {
            var outline = text.BuildGeometry(origin);
            if (outline is not null)
                context.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(220, 0, 0, 0)), 3, lineJoin: PenLineJoin.Round), outline);
            context.DrawText(text, origin);
        }
    }

    /// <summary>Урон HE и огня — у места взрыва.</summary>
    private void DrawGrenadeDamage(DrawingContext context)
    {
        if (Grenades is not { Count: > 0 } grenades)
            return;

        var byId = HighlightedGrenades is { Count: > 0 } ids ? ids : null;
        var byRound = byId is null && HighlightedRounds is { Count: > 0 } rounds ? rounds : null;
        foreach (var g in grenades)
        {
            if (g.DamageLabel is not { } label || g.To is not { } to)
                continue;
            var active = byId?.Contains(g.Id) ?? byRound?.Contains(g.Round) ?? true;
            if (!active)
                continue;
            var text = Text(label, 12, new SolidColorBrush(Color.FromRgb(255, 225, 120)), FontWeight.SemiBold);
            DrawOutlined(context, text, ToScreen(to) + new Vector(8, -text.Height - 2));
        }
    }

    /// <summary>Всплывающий урон: поднимается над жертвой и тает.</summary>
    private void DrawDamageTexts(DrawingContext context)
    {
        if (DamageTexts is not { Count: > 0 } texts)
            return;

        foreach (var d in texts)
        {
            var text = Text(d.Text, 15, new SolidColorBrush(d.Color), FontWeight.Bold);
            var origin = ToScreen(d.Position) + new Vector(-text.Width / 2, -text.Height - 10 - d.Age * 18);
            DrawOutlined(context, text, origin, Math.Clamp(1.2 - d.Age, 0, 1));
        }
    }

    /// <summary>Игрок под курсором (живой или погибший) — подсказка с ником.</summary>
    private void DrawHoveredPlayer(DrawingContext context)
    {
        if (HoveredPlayer() is not { } player)
            return;

        var text = Text(player.Tooltip, 13, Brushes.White);
        var anchor = ToScreen(player.Position);
        var rect = new Rect(anchor.X + 12, anchor.Y - text.Height - 12, text.Width + 12, text.Height + 6);
        // Не выходить за край контрола.
        if (rect.Right > Bounds.Width)
            rect = rect.WithX(Math.Max(0, anchor.X - 12 - rect.Width));
        if (rect.Y < 0)
            rect = rect.WithY(anchor.Y + 12);

        context.DrawRectangle(new SolidColorBrush(Color.FromArgb(225, 20, 24, 30)), new Pen(new SolidColorBrush(player.Color), 1), rect, 3, 3);
        context.DrawText(text, rect.TopLeft + new Vector(6, 3));
    }

    /// <summary>Ближайший к курсору маркер в радиусе 12 экранных пикселей.</summary>
    private RadarPlayerMarker? HoveredPlayer()
    {
        if (_cursorRadar is not { } cursor || _dragStart is not null || Players is not { Count: > 0 } players)
            return null;

        var limit = 12 / Scale;
        return players
            .Select(p => (Player: p, Distance: Math.Sqrt(Math.Pow(p.Position.X - cursor.X, 2) + Math.Pow(p.Position.Y - cursor.Y, 2))))
            .Where(x => x.Distance <= limit)
            .OrderBy(x => x.Player.IsDead)
            .ThenBy(x => x.Distance)
            .Select(x => x.Player)
            .FirstOrDefault();
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

    /// <summary>
    /// Гранаты: линия бросок → детонация, у дыма и огня — круг зоны действия.
    /// Выделение: явно выбранные гранаты, иначе гранаты выделенных раундов, иначе все.
    /// </summary>
    private void DrawGrenades(DrawingContext context, double scale)
    {
        if (Grenades is not { Count: > 0 } grenades)
            return;

        var byId = HighlightedGrenades is { Count: > 0 } ids ? ids : null;
        var byRound = byId is null && HighlightedRounds is { Count: > 0 } rounds ? rounds : null;
        bool IsActive(RadarGrenade g) => byId?.Contains(g.Id) ?? byRound?.Contains(g.Round) ?? true;

        foreach (var g in grenades.OrderBy(IsActive))
        {
            var active = IsActive(g);
            var color = g.Color;
            var lineBrush = new SolidColorBrush(Color.FromArgb((byte)(active ? 200 : 35), color.R, color.G, color.B));
            var fillBrush = new SolidColorBrush(Color.FromArgb((byte)(active ? 70 : 12), color.R, color.G, color.B));
            var markBrush = new SolidColorBrush(Color.FromArgb((byte)(active ? 255 : 50), color.R, color.G, color.B));

            context.DrawEllipse(markBrush, null, g.From, 2.5 / scale, 2.5 / scale);
            if (g.To is not { } to)
                continue;

            var linePen = new Pen(lineBrush, (active ? 1.6 : 1) / scale, new DashStyle([4, 3], 0));
            context.DrawLine(linePen, g.From, to);

            switch (g.Kind)
            {
                case RadarGrenadeKind.Smoke:
                case RadarGrenadeKind.Fire:
                    context.DrawEllipse(fillBrush, new Pen(lineBrush, 1.2 / scale), to, g.EffectRadius, g.EffectRadius);
                    break;
                case RadarGrenadeKind.HighExplosive:
                    DrawStar(context, markBrush, to, 6 / scale, 8);
                    break;
                case RadarGrenadeKind.Flash:
                    context.DrawEllipse(markBrush, new Pen(lineBrush, 2 / scale), to, 4 / scale, 4 / scale);
                    break;
                case RadarGrenadeKind.Decoy:
                    var s = 3.5 / scale;
                    context.DrawRectangle(null, new Pen(markBrush, 1.5 / scale), new Rect(to.X - s, to.Y - s, 2 * s, 2 * s));
                    break;
            }
        }
    }

    private void DrawDeaths(DrawingContext context, double scale)
    {
        if (Deaths is not { Count: > 0 } deaths)
            return;

        var outline = new Pen(Brushes.Black, 4 / scale);
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(255, 60, 60)), 2.2 / scale);
        var d = 6 / scale;
        foreach (var p in deaths)
        {
            foreach (var current in new[] { outline, pen })
            {
                context.DrawLine(current, p + new Vector(-d, -d), p + new Vector(d, d));
                context.DrawLine(current, p + new Vector(-d, d), p + new Vector(d, -d));
            }
        }
    }

    /// <summary>Игроки в момент таймлайна: кружок цвета стороны, у анализируемого — направление взгляда и подпись.</summary>
    private void DrawPlayers(DrawingContext context, double scale)
    {
        if (Players is not { Count: > 0 } players)
            return;

        // Погибшие — приглушённые крестики под живыми.
        foreach (var dead in players.Where(p => p.IsDead))
        {
            var deadPen = new Pen(new SolidColorBrush(Color.FromArgb(200, dead.Color.R, dead.Color.G, dead.Color.B)), 2 / scale);
            var shadow = new Pen(new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)), 3.6 / scale);
            var d = 4.5 / scale;
            foreach (var pen in new[] { shadow, deadPen })
            {
                context.DrawLine(pen, dead.Position + new Vector(-d, -d), dead.Position + new Vector(d, d));
                context.DrawLine(pen, dead.Position + new Vector(-d, d), dead.Position + new Vector(d, -d));
            }
        }

        foreach (var player in players.Where(p => !p.IsDead).OrderBy(p => p.IsSelected))
        {
            var brush = new SolidColorBrush(player.Color);
            var radius = (player.IsSelected ? 7 : 5) / scale;

            // Взгляд: игровой yaw отсчитывается против часовой от востока, ось Y радара направлена вниз.
            var angle = player.Yaw * Math.PI / 180;
            var direction = new Vector(Math.Cos(angle), -Math.Sin(angle));
            var viewLength = (player.IsSelected ? 26 : 14) / scale;
            context.DrawLine(new Pen(brush, (player.IsSelected ? 2.5 : 1.5) / scale), player.Position, player.Position + direction * viewLength);

            context.DrawEllipse(brush, new Pen(player.IsSelected ? Brushes.White : Brushes.Black, (player.IsSelected ? 2.5 : 1.2) / scale),
                player.Position, radius, radius);

            if (!player.IsSelected || player.Label.Length == 0)
                continue;

            var text = new FormattedText(player.Label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default,
                13 / scale, Brushes.White);
            var origin = player.Position + new Vector(radius + 3 / scale, -text.Height / 2);
            context.FillRectangle(new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)),
                new Rect(origin.X - 2 / scale, origin.Y, text.Width + 4 / scale, text.Height));
            context.DrawText(text, origin);
        }
    }

    private static void DrawStar(DrawingContext context, IBrush brush, Point center, double radius, int rays)
    {
        var pen = new Pen(brush, radius / 3);
        for (var i = 0; i < rays; i++)
        {
            var angle = Math.PI * 2 * i / rays;
            context.DrawLine(pen, center, center + new Vector(Math.Cos(angle) * radius, Math.Sin(angle) * radius));
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
        // Слева сверху: снизу слева — легенда радара.
        var rect = new Rect(8, 8, formatted.Width + 12, formatted.Height + 6);
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
