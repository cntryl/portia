using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

/// <summary>Describes an ordered request pipeline behavior independently of a handler.</summary>
/// <param name="scopeType">The request type or request-family interface matched by this behavior.</param>
/// <param name="behaviorType">The concrete behavior type.</param>
/// <param name="order">The behavior order; lower values execute outermost.</param>
public abstract class RequestPipelineBehaviorRegistration(Type scopeType, Type behaviorType, int order)
{
    /// <summary>Gets the request type or request-family interface matched by this behavior.</summary>
    public Type ScopeType { get; } = scopeType;

    /// <summary>Gets the concrete behavior type.</summary>
    public Type BehaviorType { get; } = behaviorType;

    /// <summary>Gets the behavior order. Lower values execute outermost.</summary>
    public int Order { get; } = order;

    internal abstract void Register(IServiceCollection services);
}
