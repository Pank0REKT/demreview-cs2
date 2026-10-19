using DemReview.Application.Localization;

namespace DemReview.Desktop.Localization;

/// <summary>
/// Строка интерфейса на текущем языке: <c>Text="{l:T 'Игроки', 'Players'}"</c>.
/// Значение берётся при загрузке разметки; при смене языка главное окно создаётся заново.
/// </summary>
public sealed class TExtension(string ru, string en)
{
    public string Ru { get; } = ru;

    public string En { get; } = en;

    public string ProvideValue(IServiceProvider serviceProvider) => Loc.T(Ru, En);
}
