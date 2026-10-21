using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DemReview.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnZoomIn(object? sender, RoutedEventArgs e) => RadarView.Zoom(1);

    private void OnZoomOut(object? sender, RoutedEventArgs e) => RadarView.Zoom(-1);

    private void OnResetView(object? sender, RoutedEventArgs e) => RadarView.ResetView();

    /// <summary>Переход к разделу настроек: Tag кнопки — имя карточки раздела.</summary>
    private void OnSettingsSection(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: string name } && this.FindControl<Control>(name) is { } section)
            SettingsScroll.Offset = SettingsScroll.Offset.WithY(section.Bounds.Top);
    }
}
