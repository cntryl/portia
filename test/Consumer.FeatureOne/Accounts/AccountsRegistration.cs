using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia.Consumer;

public static class AccountsRegistration
{
    public static IServiceCollection AddAccounts(this IServiceCollection services)
    {
        _ = services.AddContracts();
        _ = services.AddPortia()
            .AddRequestHandler<FeatureOneHandler>()
            .AddRequestHandler<DepositAccountHandler>()
            .AddRequestHandler<ScopeHandler>()
            .AddRequestHandler<NestedScopeHandler>()
            .AddRequestAuthorizer<FeatureOneAuthorizer>();
        return services;
    }
}
