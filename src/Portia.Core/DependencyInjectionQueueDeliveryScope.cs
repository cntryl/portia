using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Cntryl.Portia;

sealed class DependencyInjectionQueueDeliveryScope(AsyncServiceScope scope)
    : DependencyInjectionRequestDeliveryScope(scope), IQueueDeliveryScope
{
    public QueueRunnerOptions Options =>
        Services.GetService<IOptions<QueueRunnerOptions>>()?.Value ?? new QueueRunnerOptions();

    public IQueuedRequestTerminalHandler? TerminalHandler => Services.GetService<IQueuedRequestTerminalHandler>();
}
