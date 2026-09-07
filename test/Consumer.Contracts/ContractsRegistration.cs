using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia.Consumer;

public static class ContractsRegistration
{
    public static IServiceCollection AddContracts(this IServiceCollection services)
    {
        _ = services.AddPortia(p =>
        {
            _ = p.RegisterDynamicRequest<FeatureOneRequest>();
            _ = p.RegisterDynamicRequest<FeatureTwoRequest>();
            _ = p.RegisterDynamicRequest<DepositAccount>();
            _ = p.RegisterDynamicRequest<ScopeRequest>();
        });
        return services;
    }
}
