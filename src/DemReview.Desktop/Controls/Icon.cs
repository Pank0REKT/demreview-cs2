using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace DemReview.Desktop.Controls;

/// <summary>
/// Контурная иконка 24×24 (пути из макета), масштабируется под размер контрола.
/// Цвет — наследуемый Foreground (как у текста кнопки, включая наведение), либо явный <see cref="Brush"/>.
/// </summary>
public sealed class Icon : Control
{
    private const double ViewBox = 24;

    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<Icon, Geometry?>(nameof(Data));

    /// <summary>Толщина линии в единицах исходного поля 24×24.</summary>
    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<Icon, double>(nameof(StrokeThickness), 1.8);

    /// <summary>Залитая иконка (воспроизведение, пауза) вместо контура.</summary>
    public static readonly StyledProperty<bool> IsFilledProperty =
        AvaloniaProperty.Register<Icon, bool>(nameof(IsFilled));

    public static readonly StyledProperty<IBrush?> BrushProperty =
        AvaloniaProperty.Register<Icon, IBrush?>(nameof(Brush));

    static Icon()
    {
        AffectsRender<Icon>(DataProperty, StrokeThicknessProperty, IsFilledProperty, BrushProperty, TextElement.ForegroundProperty);
        AffectsMeasure<Icon>(DataProperty);
    }

    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public bool IsFilled
    {
        get => GetValue(IsFilledProperty);
        set => SetValue(IsFilledProperty, value);
    }

    public IBrush? Brush
    {
        get => GetValue(BrushProperty);
        set => SetValue(BrushProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(16, 16);

    public override void Render(DrawingContext context)
    {
        if (Data is not { } data || Bounds.Width <= 0 || Bounds.Height <= 0)
            return;

        var brush = Brush ?? GetValue(TextElement.ForegroundProperty);
        var scale = Math.Min(Bounds.Width, Bounds.Height) / ViewBox;
        var offset = new Vector((Bounds.Width - ViewBox * scale) / 2, (Bounds.Height - ViewBox * scale) / 2);
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offset)))
        {
            if (IsFilled)
                context.DrawGeometry(brush, null, data);
            else
                context.DrawGeometry(null, new Pen(brush, StrokeThickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), data);
        }
    }
}
