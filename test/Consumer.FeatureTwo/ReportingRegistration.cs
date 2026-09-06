using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cntryl.Portia.Consumer;

public static class ReportingRegistration
{
    public static IServiceCollection AddReporting(this IServiceCollection services)
    {
        _ = services.AddContracts();
        _ = services.AddPortia(p =>
        {
            _ = p.AddHandler<FeatureTwoHandler>();
            _ = p.AddEvent<FeatureTwoObserved>();
        });
        if (!services.Any(item => item.ServiceType == typeof(SecondReactor)))
        {
            services.TryAddScoped<SecondReactor>();
            services.TryAddScoped<SecondProjector>();
            _ = services.AddSingleton(new ReactorRegistration(typeof(SecondReactor), p => p.GetRequiredService<SecondReactor>()));
            _ = services.AddSingleton(ProjectorRegistration.Create<SecondProjector>());
        }
        return services;
    }
}
