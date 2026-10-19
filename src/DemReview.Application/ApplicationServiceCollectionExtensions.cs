using DemReview.Application.Parsing;
using Microsoft.Extensions.DependencyInjection;

namespace DemReview.Application;

public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton<IMatchLoader, MatchLoader>();
        return services;
    }
}
