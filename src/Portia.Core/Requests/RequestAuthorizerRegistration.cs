using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

/// <summary>Describes an authorizer independently of its handler.</summary>
/// <param name="requestType">The request.</param>
/// <param name="authorizerType">The authorizer.</param>
/// <param name="stage">The semantic stage at which this policy runs.</param>
public abstract class RequestAuthorizerRegistration(
    Type requestType,
    Type authorizerType,
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

    // The application's own authorizer type, not this generic registration wrapper — every
    // registration shares one wrapper type name, so reporting that would tag every authorization
    // measurement identically and name the wrong type in an uninitialized-result error.
    internal string ComponentName { get; } = authorizerType.Name;

    // Fixed for the life of the process, so it is rendered once rather than on every dispatch.
    internal string StageName { get; } = PortiaTelemetry.StageName(stage);

    internal abstract ValueTask<Result> AuthorizeAsync(IServiceProvider services, IRequestBase request,
        IRequestContext context, CancellationToken ct);

    internal abstract void Register(IServiceCollection services);
}
