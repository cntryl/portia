using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia.McpContracts;

public static class QualifiedApplication
{
    public static PortiaBuilder Add(IServiceCollection services)
    {
        _ = services.AddSingleton<QualifiedObservations>().AddScoped<QualifiedScope>().AddScoped<QualifiedWaitScope>();
        return services.AddPortia().AddRequestHandler<QualifiedChangeHandler>().AddRequestHandler<QualifiedReadHandler>()
            .AddRequestAuthorizer<QualifiedChangeAuthorizer>().AddRequestAuthorizer<QualifiedReadAuthorizer>()
            .AddRequestGuard<QualifiedGuard>().AddRequestPipelineBehavior<QualifiedBehavior>()
            .AddEvent<QualifiedChanged>();
    }
}
