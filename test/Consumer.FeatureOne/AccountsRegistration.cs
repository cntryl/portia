using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia.Consumer;

public static class AccountsRegistration
{
    public static IServiceCollection AddAccounts(this IServiceCollection services)
    {
        _ = services.AddContracts();
        _ = services.AddPortia(p =>
        {
            _ = p.AddFeatureOneHandler();
            _ = p.AddDepositAccountHandler();
            _ = p.AddScopeHandler();
            _ = p.AddNestedScopeHandler();
            _ = p.AddFeatureOneAuthorizer();
            _ = p.AddGeneratedEvents();
        });
        return services;
    }
}
