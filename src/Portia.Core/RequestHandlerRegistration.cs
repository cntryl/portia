using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

/// <summary>Describes a compile-time-discovered handler without constructing application dependencies.</summary>
/// <param name="requestType">The concrete request.</param>
/// <param name="handlerType">The concrete handler.</param>
/// <param name="resultType">The unary result or streaming item type, when present.</param>
/// <param name="permission">The generated permission expression, if declared.</param>
public abstract class RequestHandlerRegistration(
    Type requestType,
    Type handlerType,
    Type? resultType,
    Func<IRequestBase, string>? permission)
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
