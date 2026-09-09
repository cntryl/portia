using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

/// <summary>Describes how one registered reactor binds, subscribes, loads progress, and runs a pass.</summary>
public sealed class ReactorRegistration : IWorkloadDescriptor
{
    readonly Func<IServiceProvider, BaseReactor> _resolve;
    readonly Func<IServiceProvider, ProjectionRunOptions, CancellationToken, ValueTask> _runPass;

    ReactorRegistration(Type reactorType, Func<IServiceProvider, BaseReactor> resolve,
        Func<IServiceProvider, ProjectionRunOptions, CancellationToken, ValueTask> runPass)
    {
        ReactorType = reactorType;
        _resolve = resolve;
        _runPass = runPass;
    }

    /// <summary>Gets the concrete reactor type.</summary>
    public Type ReactorType { get; }

    /// <summary>Resolves the reactor in the supplied application scope.</summary>
    public BaseReactor Resolve(IServiceProvider services) => _resolve(services);

    /// <summary>Creates a typed reactor descriptor.</summary>
    public static ReactorRegistration Create<TReactor>() where TReactor : BaseReactor => new(
        typeof(TReactor), static services => services.GetRequiredService<TReactor>(),
        static async (services, options, ct) =>
        {
            options.Validate();
            var reactor = services.GetRequiredService<TReactor>();
            var checkpoint = await reactor.Checkpoints.LoadAsync(
                new CheckpointIdentity(reactor.Name, reactor.Pattern), ct).ConfigureAwait(false);
            _ = await services.GetRequiredService<ReactorRunner>().RunAsync(
                reactor, checkpoint, options.MaxBatchSize, ct).ConfigureAwait(false);
        });

    Type IWorkloadDescriptor.ComponentType => ReactorType;
    void IWorkloadDescriptor.Bind(IServiceProvider services, WorkloadIdentity identity, string? componentName)
        => _resolve(services).BindWorkload(identity, componentName);
    EventStreamPattern IWorkloadDescriptor.Pattern(IServiceProvider services) => _resolve(services).Pattern;
    ValueTask IWorkloadDescriptor.RunPass(IServiceProvider services, ProjectionRunOptions options, CancellationToken ct)
        => _runPass(services, options, ct);
}
