using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia.Consumer;

public static class AccountsRegistration
{
    public static IServiceCollection AddAccounts(this IServiceCollection services)
    {
        _ = services.AddContracts();
        _ = services.AddPortia(p =>
        {
            _ = p.AddRequestHandler<FeatureOneHandler>();
            _ = p.AddRequestHandler<DepositAccountHandler>();
            _ = p.AddRequestHandler<ScopeHandler>();
            _ = p.AddRequestHandler<NestedScopeHandler>();
            _ = p.AddRequestAuthorizer<FeatureOneAuthorizer>();
        });
        return services;
    }
}
