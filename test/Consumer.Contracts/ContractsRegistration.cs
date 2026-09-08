using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia.Consumer;

public static class ContractsRegistration
{
    public static IServiceCollection AddContracts(this IServiceCollection services)
    {
        _ = services.AddPortia()
            .RegisterDynamicRequest<FeatureOneRequest>()
            .RegisterDynamicRequest<FeatureTwoRequest>()
            .RegisterDynamicRequest<DepositAccount>()
            .RegisterDynamicRequest<ScopeRequest>();
        return services;
    }
}
