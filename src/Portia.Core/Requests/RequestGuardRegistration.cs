using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

/// <summary>Describes a request guard independently of a handler.</summary>
/// <param name="scopeType">The request type or request-family interface matched by this guard.</param>
/// <param name="guardType">The concrete guard type.</param>
public abstract class RequestGuardRegistration(Type scopeType, Type guardType) : IRequestCheck
{
    /// <summary>Gets the request type or request-family interface matched by this guard.</summary>
    public Type ScopeType { get; } = scopeType;

    /// <summary>Gets the concrete guard type.</summary>
    public Type GuardType { get; } = guardType;

    internal string ComponentName { get; } = guardType.Name;

    internal abstract ValueTask<Result> GuardAsync(IServiceProvider services, IRequestBase request,
        IRequestContext context, CancellationToken ct);

    internal abstract void Register(IServiceCollection services);

    Type IRequestCheck.Owner => GuardType;

    string IRequestCheck.Role => "request guard";

    ValueTask<Result> IRequestCheck.CheckAsync(IServiceProvider services, IRequestBase request,
        IRequestContext context, CancellationToken ct) => GuardAsync(services, request, context, ct);

    void IRequestCheck.Finished(long started, string outcome) =>
        PortiaTelemetry.GuardFinished(started, ComponentName, outcome);
}
