using DemReview.Application.Grenades;
using DemReview.Application.Maps;
using DemReview.Application.Parsing;
using DemReview.Application.Routes;
using Microsoft.Extensions.DependencyInjection;

namespace DemReview.Application;

public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton<IMatchLoader, MatchLoader>();
        services.AddSingleton<IMapCoordinateConverter, MapCoordinateConverter>();
        services.AddSingleton<IRouteAnalysisService, RouteAnalysisService>();
        services.AddSingleton<IGrenadeAnalysisService, GrenadeAnalysisService>();
        return services;
    }
}
