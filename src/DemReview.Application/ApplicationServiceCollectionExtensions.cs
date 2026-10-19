using DemReview.Application.Context;
using DemReview.Application.Grenades;
using DemReview.Application.Heatmaps;
using DemReview.Application.Maps;
using DemReview.Application.Parsing;
using DemReview.Application.Routes;
using DemReview.Application.Situations;
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
        services.AddSingleton<IRoundContextService, RoundContextService>();
        services.AddSingleton<ISituationAnalysisService, SituationAnalysisService>();
        services.AddSingleton<IHeatmapService, HeatmapService>();
        return services;
    }
}
