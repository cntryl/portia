using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cntryl.Portia;

/// <summary>Invokes one generated streaming pipeline behavior registration.</summary>
public sealed class StreamRequestPipelineBehaviorRegistration<TRequest,
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TBehavior, TOut>(int order)
    : RequestPipelineBehaviorRegistration(typeof(TRequest), typeof(TBehavior), order),
        IStreamRequestBehaviorInvocation<TOut>
    where TRequest : IStreamRequest<TOut>
    where TBehavior : class, IStreamRequestPipelineBehavior<TRequest, TOut>
{
    IAsyncEnumerable<TOut> IStreamRequestBehaviorInvocation<TOut>.Invoke(IServiceProvider services,
        IStreamRequest<TOut> request, RequestDispatchContext context, StreamRequestPipelineNext<TOut> continuation,
        CancellationToken ct)
        => services.GetRequiredService<TBehavior>()
            .HandleAsync(new RequestContext<TRequest>((TRequest)request, context), continuation, ct);

    internal override void Register(IServiceCollection services) => services.TryAddScoped<TBehavior>();
}
