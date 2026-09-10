using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cntryl.Portia;

/// <summary>Invokes one generated streaming handler registration from the current scope.</summary>
/// <typeparam name="TRequest">The request.</typeparam>
/// <typeparam name="THandler">The handler.</typeparam>
/// <typeparam name="TOut">The streamed item.</typeparam>
/// <param name="permission">The generated permission expression.</param>
public sealed class StreamRequestRegistration<TRequest,
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler, TOut>(
    Func<TRequest, string>? permission = null)
    : RequestHandlerRegistration(typeof(TRequest), typeof(THandler), typeof(TOut),
        permission is null ? null : request => permission((TRequest)request)), IStreamRequestInvocation<TOut>
    where TRequest : IStreamRequest<TOut>
    where THandler : class, IStreamRequestHandler<TRequest, TOut>
{
    IAsyncEnumerable<TOut> IStreamRequestInvocation<TOut>.Invoke(IServiceProvider services,
        IStreamRequest<TOut> request, RequestDispatchContext context, CancellationToken ct)
        => services.GetRequiredService<THandler>()
            .HandleAsync(new RequestContext<TRequest>((TRequest)request, context), ct);

    internal override void Register(IServiceCollection services) => services.TryAddScoped<THandler>();
}
