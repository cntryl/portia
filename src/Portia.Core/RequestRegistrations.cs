using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cntryl.Portia;

/// <summary>Describes a compile-time-discovered handler without constructing application dependencies.</summary>
/// <param name="requestType">The concrete request.</param>
/// <param name="handlerType">The concrete handler.</param>
/// <param name="resultType">The unary result or streaming item type, when present.</param>
/// <param name="permission">The generated permission expression, if declared.</param>
public abstract class RequestHandlerRegistration(Type requestType, Type handlerType, Type? resultType, Func<IRequestBase, string>? permission)
{
    /// <summary>Gets the concrete request type.</summary>
    public Type RequestType { get; } = requestType;

    /// <summary>Gets the concrete handler type.</summary>
    public Type HandlerType { get; } = handlerType;

    /// <summary>Gets the unary result or streaming item type, when the request produces one.</summary>
    public Type? ResultType { get; } = resultType;

    internal Func<IRequestBase, string>? Permission { get; } = permission;

    internal abstract void Register(IServiceCollection services);
}

interface IRequestInvocation
{
    ValueTask<Result> InvokeAsync(IServiceProvider services, IRequest request, RequestDispatchContext context, CancellationToken ct);
}

interface IRequestInvocation<TOut>
{
    ValueTask<Result<TOut>> InvokeAsync(IServiceProvider services, IRequest<TOut> request, RequestDispatchContext context, CancellationToken ct);
}

interface IStreamRequestInvocation<TOut>
{
    IAsyncEnumerable<TOut> Invoke(IServiceProvider services, IStreamRequest<TOut> request, RequestDispatchContext context, CancellationToken ct);
}

/// <summary>Invokes one generated no-result handler registration from the current scope.</summary>
/// <typeparam name="TRequest">The request.</typeparam>
/// <typeparam name="THandler">The handler.</typeparam>
/// <param name="permission">The generated permission expression.</param>
public sealed class RequestRegistration<TRequest, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler>(Func<TRequest, string>? permission = null)
    : RequestHandlerRegistration(typeof(TRequest), typeof(THandler), null, permission is null ? null : request => permission((TRequest)request)), IRequestInvocation
    where TRequest : IRequest
    where THandler : class, IRequestHandler<TRequest>
{
    internal override void Register(IServiceCollection services) => services.TryAddScoped<THandler>();

    ValueTask<Result> IRequestInvocation.InvokeAsync(IServiceProvider services, IRequest request, RequestDispatchContext context, CancellationToken ct)
        => services.GetRequiredService<THandler>().HandleAsync(new RequestContext<TRequest>((TRequest)request, context), ct);
}

/// <summary>Invokes one generated result handler registration from the current scope.</summary>
/// <typeparam name="TRequest">The request.</typeparam>
/// <typeparam name="THandler">The handler.</typeparam>
/// <typeparam name="TOut">The result.</typeparam>
/// <param name="permission">The generated permission expression.</param>
public sealed class RequestRegistration<TRequest, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler, TOut>(Func<TRequest, string>? permission = null)
    : RequestHandlerRegistration(typeof(TRequest), typeof(THandler), typeof(TOut), permission is null ? null : request => permission((TRequest)request)), IRequestInvocation<TOut>
    where TRequest : IRequest<TOut>
    where THandler : class, IRequestHandler<TRequest, TOut>
{
    internal override void Register(IServiceCollection services) => services.TryAddScoped<THandler>();

    ValueTask<Result<TOut>> IRequestInvocation<TOut>.InvokeAsync(IServiceProvider services, IRequest<TOut> request, RequestDispatchContext context, CancellationToken ct)
        => services.GetRequiredService<THandler>().HandleAsync(new RequestContext<TRequest>((TRequest)request, context), ct);
}

/// <summary>Invokes one generated streaming handler registration from the current scope.</summary>
/// <typeparam name="TRequest">The request.</typeparam>
/// <typeparam name="THandler">The handler.</typeparam>
/// <typeparam name="TOut">The streamed item.</typeparam>
/// <param name="permission">The generated permission expression.</param>
public sealed class StreamRequestRegistration<TRequest, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler, TOut>(Func<TRequest, string>? permission = null)
    : RequestHandlerRegistration(typeof(TRequest), typeof(THandler), typeof(TOut), permission is null ? null : request => permission((TRequest)request)), IStreamRequestInvocation<TOut>
    where TRequest : IStreamRequest<TOut>
    where THandler : class, IStreamRequestHandler<TRequest, TOut>
{
    internal override void Register(IServiceCollection services) => services.TryAddScoped<THandler>();

    IAsyncEnumerable<TOut> IStreamRequestInvocation<TOut>.Invoke(IServiceProvider services, IStreamRequest<TOut> request, RequestDispatchContext context, CancellationToken ct)
        => services.GetRequiredService<THandler>().HandleAsync(new RequestContext<TRequest>((TRequest)request, context), ct);
}

/// <summary>Describes an authorizer independently of its handler.</summary>
/// <param name="requestType">The request.</param>
/// <param name="authorizerType">The authorizer.</param>
/// <param name="stage">The semantic stage at which this policy runs.</param>
public abstract class RequestAuthorizerRegistration(Type requestType, Type authorizerType,
    AuthorizationStage stage = AuthorizationStage.ResourceAccess)
{
    /// <summary>Gets the authorized request type.</summary>
    public Type RequestType { get; } = requestType;

    /// <summary>Gets the concrete authorizer.</summary>
    public Type AuthorizerType { get; } = authorizerType;

    /// <summary>Gets the request family this policy applies to.</summary>
    public Type ScopeType => RequestType;

    /// <summary>Gets the semantic stage at which the policy runs.</summary>
    public AuthorizationStage Stage { get; } = stage;

    internal abstract ValueTask<Result> AuthorizeAsync(IServiceProvider services, IRequestBase request, RequestDispatchContext context, CancellationToken ct);

    internal abstract void Register(IServiceCollection services);
}

/// <summary>Resolves and invokes the selected authorizer in the current scope.</summary>
/// <typeparam name="TRequest">The request.</typeparam>
/// <typeparam name="TAuthorizer">The authorizer.</typeparam>
public sealed class RequestAuthorizerRegistration<TRequest, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TAuthorizer>(AuthorizationStage stage = AuthorizationStage.ResourceAccess)
    : RequestAuthorizerRegistration(typeof(TRequest), typeof(TAuthorizer), stage)
    where TRequest : IRequestBase
    where TAuthorizer : class, IRequestAuthorizer<TRequest>
{
    internal override void Register(IServiceCollection services) => services.TryAddScoped<TAuthorizer>();

    internal override ValueTask<Result> AuthorizeAsync(IServiceProvider services, IRequestBase request, RequestDispatchContext context, CancellationToken ct)
        => services.GetRequiredService<TAuthorizer>().AuthorizeAsync(new RequestContext<TRequest>((TRequest)request, context), context.Actor, ct);
}
