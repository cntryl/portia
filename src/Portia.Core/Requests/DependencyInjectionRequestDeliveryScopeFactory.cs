using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

sealed class DependencyInjectionRequestDeliveryScopeFactory(IServiceScopeFactory scopes) : IRequestDeliveryScopeFactory
{
    public ValueTask<IRequestDeliveryScope> CreateAsync(CancellationToken ct = default)
        => ValueTask.FromResult<IRequestDeliveryScope>(
            new DependencyInjectionRequestDeliveryScope(scopes.CreateAsyncScope()));
}
