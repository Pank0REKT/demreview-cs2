using Avalonia.Data.Converters;
using Avalonia.Media;
using DemReview.Application.Localization;
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
        building ? Loc.T("Строю профиль игрока…", "Building the player profile…") : Loc.T("Выберите игрока в списке слева.", "Select a player in the list on the left."));
}
