using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cntryl.Portia;

/// <summary>Invokes one generated no-result handler registration from the current scope.</summary>
/// <typeparam name="TRequest">The request.</typeparam>
/// <typeparam name="THandler">The handler.</typeparam>
/// <param name="permission">The generated permission expression.</param>
public sealed class RequestRegistration<TRequest,
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler>(
    Func<TRequest, string>? permission = null)
    : RequestHandlerRegistration(typeof(TRequest), typeof(THandler), null,
        permission is null ? null : request => permission((TRequest)request)), IRequestInvocation
    where TRequest : IRequest
    where THandler : class, IRequestHandler<TRequest>
{
    ValueTask<Result> IRequestInvocation.InvokeAsync(IServiceProvider services, IRequest request,
        RequestDispatchContext context, CancellationToken ct)
        => services.GetRequiredService<THandler>()
            .HandleAsync(new RequestContext<TRequest>((TRequest)request, context), ct);

    internal override void Register(IServiceCollection services) => services.TryAddScoped<THandler>();
}
