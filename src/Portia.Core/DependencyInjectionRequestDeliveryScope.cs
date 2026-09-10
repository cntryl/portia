using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

class DependencyInjectionRequestDeliveryScope(AsyncServiceScope scope) : IRequestDeliveryScope
{
    protected IServiceProvider Services => scope.ServiceProvider;
    public IRequestBus Bus => Services.GetRequiredService<IRequestBus>();
    public IRequestActorValidator ActorValidator => Services.GetRequiredService<IRequestActorValidator>();
    public TimeProvider? TimeProvider => Services.GetService<TimeProvider>();
    public ValueTask DisposeAsync() => scope.DisposeAsync();
}
