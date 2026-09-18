using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

/// <summary>
///     What a hosted workload needs to know about one registered component, so hosting never asks
///     which kind of component it is. A descriptor answers for its own kind; adding a kind means
///     adding a descriptor, not another branch.
/// </summary>
interface IWorkloadDescriptor
{
    Type ComponentType { get; }

    /// <summary>Gets whether this kind of component can run a separate rebuild generation.</summary>
    bool SupportsRebuild { get; }

    void Bind(IServiceProvider services, WorkloadIdentity identity, string? componentName);
    EventStreamPattern Pattern(IServiceProvider services);

    /// <summary>
    ///     Checks at host startup, without binding anything, that the component resolved in
    ///     <paramref name="services" /> can run under <paramref name="componentName" />.
    /// </summary>
    /// <param name="services">A validation scope.</param>
    /// <param name="componentName">The name the component is registered under.</param>
    void Validate(IServiceProvider services, string componentName);

    ValueTask<ProjectionPassResult> RunPass(IServiceProvider services, ProjectionRunOptions options,
        CancellationToken ct);

    /// <summary>Registers this descriptor under its own concrete type for the container.</summary>
    /// <param name="services">The application's service collection.</param>
    void Register(IServiceCollection services);
}
