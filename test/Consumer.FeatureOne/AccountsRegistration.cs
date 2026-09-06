using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cntryl.Portia.Consumer;

public static class AccountsRegistration
{
    public static IServiceCollection AddAccounts(this IServiceCollection services)
    {
        _ = services.AddContracts();
        _ = services.AddPortia(p =>
        {
            _ = p.AddHandler<FeatureOneHandler>();
            _ = p.AddHandler<DepositAccountHandler>();
            _ = p.AddHandler<ScopeHandler>();
            _ = p.AddHandler<NestedScopeHandler>();
            _ = p.AddAuthorizer<FeatureOneAuthorizer>();
            _ = p.AddEvent<FeatureOneObserved>();
        });
        if (!services.Any(item => item.ServiceType == typeof(FirstReactor)))
        {
            services.TryAddScoped<FirstReactor>();
            services.TryAddScoped<FirstProjector>();
            _ = services.AddSingleton(new ReactorRegistration(typeof(FirstReactor), p => p.GetRequiredService<FirstReactor>()));
            _ = services.AddSingleton(ProjectorRegistration.Create<FirstProjector>());
        }
        return services;
    }
}
