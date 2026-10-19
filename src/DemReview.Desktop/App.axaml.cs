using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using DemReview.Application;
using DemReview.Desktop.Behaviors;
using DemReview.Desktop.Services;
using DemReview.Desktop.ViewModels;
using DemReview.Desktop.Views;
using Microsoft.Extensions.DependencyInjection;

namespace DemReview.Desktop;

public partial class App : Avalonia.Application
{
    /// <summary>Контейнер DI, созданный хостом в <see cref="Program"/>.</summary>
    public static IServiceProvider Services { get; set; } = null!;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = Services.GetRequiredService<MainWindowViewModel>();
            var platform = Services.GetRequiredService<IPlatformService>();
            var window = new MainWindow
            {
                DataContext = viewModel,
                Icon = new WindowIcon(AssetLoader.Open(new Uri($"avares://DemReview/Assets/{platform.WindowIconFileName}"))),
            };

            Services.GetRequiredService<MainWindowAccessor>().Window = window;
            WindowPlacementTracker.Attach(window, Services.GetRequiredService<ISettingsService>());
            window.Opened += async (_, _) => await viewModel.InitializeAsync();
            window.Closing += (_, _) => viewModel.CancelCommand.Execute(null);

            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
