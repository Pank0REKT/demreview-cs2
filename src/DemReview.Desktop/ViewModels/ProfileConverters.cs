using Avalonia.Data.Converters;
using Avalonia.Media;
using DemReview.Application.Patterns;

namespace DemReview.Desktop.ViewModels;

public static class ProfileConverters
{
    /// <summary>Цвет точки уверенности паттерна: высокая — зелёный, средняя — жёлтый, низкая — серый.</summary>
    public static readonly IValueConverter ConfidenceBrush = new FuncValueConverter<Confidence, IBrush>(c => c switch
    {
        Confidence.High => new SolidColorBrush(Color.FromRgb(90, 200, 110)),
        Confidence.Medium => new SolidColorBrush(Color.FromRgb(235, 190, 70)),
        _ => new SolidColorBrush(Color.FromRgb(130, 135, 145)),
    });

    /// <summary>Текст пустой вкладки профиля.</summary>
    public static readonly IValueConverter EmptyText = new FuncValueConverter<bool, string>(building =>
        building ? "Строю профиль игрока…" : "Выберите игрока в списке слева.");
}
