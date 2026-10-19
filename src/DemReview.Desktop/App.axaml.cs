using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using DemReview.Application;
using DemReview.Application.Localization;
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
            var language = Services.GetRequiredService<LanguageService>();
            if (language.Saved is { } saved)
            {
                language.Apply(saved, save: false);
                desktop.MainWindow = CreateMainWindow(desktop);
                SubscribeLanguageChanges(desktop);
            }
            else
            {
                desktop.MainWindow = CreateLanguageWindow(desktop, language);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Первый запуск: выбор языка. Главное окно показывается до закрытия окна выбора, чтобы приложение
    /// не завершилось; закрытие без выбора — язык системы, и при следующем запуске вопрос повторится.
    /// </summary>
    private Window CreateLanguageWindow(IClassicDesktopStyleApplicationLifetime desktop, LanguageService language)
    {
        var picker = new LanguageWindow(Loc.FromSystem());
        SetIcon(picker);
        picker.Closing += (_, _) =>
        {
            if (desktop.MainWindow != picker)
                return;
            language.Apply(picker.Result ?? picker.Suggested, save: picker.Result is not null);
            var main = CreateMainWindow(desktop);
            desktop.MainWindow = main;
            main.Show();
            SubscribeLanguageChanges(desktop);
        };
        return picker;
    }

    private void SubscribeLanguageChanges(IClassicDesktopStyleApplicationLifetime desktop) =>
        Loc.Changed += () => Dispatcher.UIThread.Post(() => RecreateMainWindow(desktop));

    private MainWindow CreateMainWindow(IClassicDesktopStyleApplicationLifetime desktop, bool initialize = true)
    {
        var viewModel = Services.GetRequiredService<MainWindowViewModel>();
        var window = new MainWindow { DataContext = viewModel };
        SetIcon(window);

        Services.GetRequiredService<MainWindowAccessor>().Window = window;
        WindowPlacementTracker.Attach(window, Services.GetRequiredService<ISettingsService>());
        if (initialize)
            window.Opened += async (_, _) => await viewModel.InitializeAsync();
        window.Closing += (_, _) =>
        {
            // Закрытие окна при смене языка не должно прерывать разбор демки.
            if (desktop.MainWindow == window)
                viewModel.CancelCommand.Execute(null);
        };
        return window;
    }

    /// <summary>
    /// Смена языка: тексты анализа пересчитываются, окно создаётся заново с тем же положением и размером —
    /// так все подписи разметки гарантированно берутся на новом языке.
    /// </summary>
    private void RecreateMainWindow(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (desktop.MainWindow is not MainWindow old)
            return;

        Services.GetRequiredService<MainWindowViewModel>().RefreshLanguage();

        var window = CreateMainWindow(desktop, initialize: false);
        if (old.WindowState == WindowState.Maximized)
        {
            // Обычный размер уже восстановлен из настроек — окно просто разворачивается.
            window.WindowState = WindowState.Maximized;
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Position = old.Position;
            window.Width = old.Width;
            window.Height = old.Height;
            window.WindowState = WindowState.Normal;
        }

        desktop.MainWindow = window;
        window.Show();
        old.Close();
    }

    private static void SetIcon(Window window)
    {
        var platform = Services.GetRequiredService<IPlatformService>();
        window.Icon = new WindowIcon(AssetLoader.Open(new Uri($"avares://DemReview/Assets/{platform.WindowIconFileName}")));
    }
}
