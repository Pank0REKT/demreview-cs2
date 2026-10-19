using Avalonia;
using DemReview.Application;
using DemReview.Desktop.Services;
using DemReview.Desktop.ViewModels;
using DemReview.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace DemReview.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var platform = InfrastructureServiceCollectionExtensions.CreatePlatformService();
        Log.Logger = CreateLogger(platform);
        Log.Information("Запуск DemReview {Version} на {Platform}", typeof(Program).Assembly.GetName().Version, platform.PlatformName);

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Fatal(e.ExceptionObject as Exception, "Необработанное исключение");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error(e.Exception, "Ненаблюдаемое исключение задачи");
            e.SetObserved();
        };

        try
        {
            using var host = CreateHost(args, platform);
            host.Start();
            App.Services = host.Services;

            var exitCode = BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

            host.StopAsync().GetAwaiter().GetResult();
            return exitCode;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Приложение аварийно завершено");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    // Используется также визуальным дизайнером Avalonia.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static IHost CreateHost(string[] args, IPlatformService platform)
    {
        // DisableDefaults: без appsettings, переменных окружения и прочих внешних источников конфигурации.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true, Args = args });

        builder.Logging.ClearProviders();
        builder.Logging.AddSerilog(Log.Logger, dispose: false);

        builder.Services
            .AddApplication()
            .AddInfrastructure()
            .AddSingleton(new StartupOptions(StartupOptions.FindDemoPath(args)))
            .AddSingleton<IUiDispatcher, AvaloniaUiDispatcher>()
            .AddSingleton<MainWindowAccessor>()
            .AddSingleton<IFileDialogService, AvaloniaFileDialogService>()
            .AddSingleton<RadarViewModel>()
            .AddSingleton<StatisticsViewModel>()
            .AddSingleton<LanguageService>()
            .AddSingleton<ProfileViewModel>()
            .AddSingleton<SettingsViewModel>()
            .AddSingleton<MainWindowViewModel>();

        // Сервис платформы уже создан для логгера — регистрируем тот же экземпляр.
        builder.Services.AddSingleton(platform);
        return builder.Build();
    }

    private static Serilog.ILogger CreateLogger(IPlatformService platform) =>
        new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(platform.AppDataDirectory, "logs", "demreview-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                encoding: System.Text.Encoding.UTF8,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
}
