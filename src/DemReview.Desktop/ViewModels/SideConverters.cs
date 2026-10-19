using Avalonia.Data.Converters;
using DemReview.Domain;

namespace DemReview.Desktop.ViewModels;

/// <summary>Конвертеры стороны для раскраски T/CT в разметке.</summary>
public static class SideConverters
{
    public static readonly IValueConverter IsT = new FuncValueConverter<Side, bool>(side => side == Side.T);

    public static readonly IValueConverter IsCT = new FuncValueConverter<Side, bool>(side => side == Side.CT);
}
