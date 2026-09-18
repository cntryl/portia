using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

sealed class DependencyInjectionQueueDeliveryScopeFactory(IServiceScopeFactory scopes) : IQueueDeliveryScopeFactory
{
    public ValueTask<IQueueDeliveryScope> CreateAsync(CancellationToken ct = default)
        => ValueTask.FromResult<IQueueDeliveryScope>(
            new DependencyInjectionQueueDeliveryScope(scopes.CreateAsyncScope()));
}
