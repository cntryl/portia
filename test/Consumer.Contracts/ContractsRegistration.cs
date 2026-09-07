using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia.Consumer;

public static class ContractsRegistration
{
    public static IServiceCollection AddContracts(this IServiceCollection services)
    {
        _ = services.AddPortia(p =>
        {
            _ = p.AddGeneratedEvents();
            _ = p.AddFeatureOneRequest();
            _ = p.AddFeatureTwoRequest();
            _ = p.AddDepositAccount();
            _ = p.AddScopeRequest();
        });
        return services;
    }
}
