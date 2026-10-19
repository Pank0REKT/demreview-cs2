using DemReview.Application;
using DemReview.Application.Maps;
using DemReview.Application.Parsing;
using DemReview.Infrastructure.Maps;
using DemReview.Infrastructure.Parsing;
using DemReview.Infrastructure.Platform;
using DemReview.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace DemReview.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton(CreatePlatformService());
        services.AddSingleton<ISettingsService, JsonSettingsService>();
        services.AddSingleton<IMatchCache, SqliteMatchCache>();
        services.AddSingleton<IDemoParser, DemoFileDemoParser>();
        services.AddSingleton<IMapConfigProvider, EmbeddedMapConfigProvider>();
        services.AddSingleton<IMapImageProvider, LocalMapImageProvider>();
        return services;
    }

    public static IPlatformService CreatePlatformService()
    {
        if (OperatingSystem.IsWindows())
            return new WindowsPlatformService();
        if (OperatingSystem.IsLinux())
            return new LinuxPlatformService();

        throw new PlatformNotSupportedException("Поддерживаются только Windows и Linux.");
    }
}
