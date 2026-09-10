using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cntryl.Portia;

interface IRequestInvocation<TOut>
{
    ValueTask<Result<TOut>> InvokeAsync(IServiceProvider services, IRequest<TOut> request,
        RequestDispatchContext context, CancellationToken ct);
}

/// <summary>Invokes one generated result handler registration from the current scope.</summary>
/// <typeparam name="TRequest">The request.</typeparam>
/// <typeparam name="THandler">The handler.</typeparam>
/// <typeparam name="TOut">The result.</typeparam>
/// <param name="permission">The generated permission expression.</param>
public sealed class RequestRegistration<TRequest,
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler, TOut>(
    Func<TRequest, string>? permission = null)
    : RequestHandlerRegistration(typeof(TRequest), typeof(THandler), typeof(TOut),
        permission is null ? null : request => permission((TRequest)request)), IRequestInvocation<TOut>
    where TRequest : IRequest<TOut>
    where THandler : class, IRequestHandler<TRequest, TOut>
{
    ValueTask<Result<TOut>> IRequestInvocation<TOut>.InvokeAsync(IServiceProvider services, IRequest<TOut> request,
        RequestDispatchContext context, CancellationToken ct)
        => services.GetRequiredService<THandler>()
            .HandleAsync(new RequestContext<TRequest>((TRequest)request, context), ct);

    internal override void Register(IServiceCollection services) => services.TryAddScoped<THandler>();
}

/// <summary>Resolves and invokes the selected authorizer in the current scope.</summary>
/// <typeparam name="TRequest">The request.</typeparam>
/// <typeparam name="TAuthorizer">The authorizer.</typeparam>
public sealed class RequestAuthorizerRegistration<TRequest,
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TAuthorizer>(
    AuthorizationStage stage = AuthorizationStage.ResourceAccess)
    : RequestAuthorizerRegistration(typeof(TRequest), typeof(TAuthorizer), stage)
    where TRequest : IRequestBase
    where TAuthorizer : class, IRequestAuthorizer<TRequest>
{
    internal override void Register(IServiceCollection services) => services.TryAddScoped<TAuthorizer>();

    internal override ValueTask<Result> AuthorizeAsync(IServiceProvider services, IRequestBase request,
        RequestDispatchContext context, CancellationToken ct)
        => services.GetRequiredService<TAuthorizer>()
            .AuthorizeAsync(new RequestContext<TRequest>((TRequest)request, context), ct);
}

interface IRequestBehaviorInvocation<TOut>
{
    ValueTask<Result<TOut>> InvokeAsync(IServiceProvider services, IRequest<TOut> request,
        RequestDispatchContext context, RequestPipelineNext<TOut> continuation, CancellationToken ct);
}

/// <summary>Invokes one generated no-result pipeline behavior registration.</summary>
public sealed class RequestPipelineBehaviorRegistration<TRequest,
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TBehavior>(int order)
    : RequestPipelineBehaviorRegistration(typeof(TRequest), typeof(TBehavior), order), IRequestBehaviorInvocation
    where TRequest : IRequest
    where TBehavior : class, IRequestPipelineBehavior<TRequest>
{
    ValueTask<Result> IRequestBehaviorInvocation.InvokeAsync(IServiceProvider services, IRequest request,
        RequestDispatchContext context, RequestPipelineNext continuation, CancellationToken ct)
        => services.GetRequiredService<TBehavior>()
            .HandleAsync(new RequestContext<TRequest>((TRequest)request, context), continuation, ct);

    internal override void Register(IServiceCollection services) => services.TryAddScoped<TBehavior>();
}

/// <summary>Invokes one generated result-bearing pipeline behavior registration.</summary>
public sealed class RequestPipelineBehaviorRegistration<TRequest,
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TBehavior, TOut>(int order)
    : RequestPipelineBehaviorRegistration(typeof(TRequest), typeof(TBehavior), order), IRequestBehaviorInvocation<TOut>
    where TRequest : IRequest<TOut>
    where TBehavior : class, IRequestPipelineBehavior<TRequest, TOut>
{
    ValueTask<Result<TOut>> IRequestBehaviorInvocation<TOut>.InvokeAsync(IServiceProvider services,
        IRequest<TOut> request, RequestDispatchContext context, RequestPipelineNext<TOut> continuation,
        CancellationToken ct)
        => services.GetRequiredService<TBehavior>()
            .HandleAsync(new RequestContext<TRequest>((TRequest)request, context), continuation, ct);

    internal override void Register(IServiceCollection services) => services.TryAddScoped<TBehavior>();
}
