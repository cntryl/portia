using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia.Consumer;

public static class ReportingRegistration
{
    public static IServiceCollection AddReporting(this IServiceCollection services)
    {
        _ = services.AddContracts();
        _ = services.AddPortia(p => _ = p.AddRequestHandler<FeatureTwoHandler>());
        return services;
    }
}
