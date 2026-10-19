using Avalonia.Controls;
using Avalonia.Interactivity;
using DemReview.Application.Localization;

namespace DemReview.Desktop.Views;

/// <summary>Выбор языка при первом запуске.</summary>
public partial class LanguageWindow : Window
{
    public LanguageWindow()
        : this(UiLanguage.Russian)
    {
    }

    /// <param name="suggested">Язык по умолчанию (по языку системы) — на его кнопке фокус.</param>
    public LanguageWindow(UiLanguage suggested)
    {
        InitializeComponent();
        Suggested = suggested;
        Opened += (_, _) => (suggested == UiLanguage.English ? EnglishButton : RussianButton).Focus();
    }

    public UiLanguage Suggested { get; }

    /// <summary>Выбранный язык; null — окно закрыто без выбора.</summary>
    public UiLanguage? Result { get; private set; }

    private void OnRussian(object? sender, RoutedEventArgs e) => Choose(UiLanguage.Russian);

    private void OnEnglish(object? sender, RoutedEventArgs e) => Choose(UiLanguage.English);

    private void Choose(UiLanguage language)
    {
        Result = language;
        Close();
    }
}
