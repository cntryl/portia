using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia.Consumer;

public static class ContractsRegistration
{
    public static IServiceCollection AddContracts(this IServiceCollection services)
    {
        _ = services.AddPortia(p =>
        {
            _ = p.AddEvent<Deposited>();
            _ = p.AddEvent<Declined>();
            _ = p.AddRequest<FeatureOneRequest>();
            _ = p.AddRequest<FeatureTwoRequest>();
            _ = p.AddRequest<DepositAccount>();
            _ = p.AddRequest<ScopeRequest>();
        });
        return services;
    }
}
