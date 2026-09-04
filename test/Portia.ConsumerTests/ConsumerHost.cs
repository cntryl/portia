using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia.Consumer;

static class ConsumerHost
{
    public static ServiceCollection CreateServices(bool scoped = true)
    {
        var services = new ServiceCollection();
        _ = services.AddPortiaAggregate((_, id) => new Account(id));
        _ = services.AddSingleton<Effects>();
        _ = services.AddSingleton<IConsumerEffects>(provider => provider.GetRequiredService<Effects>());
        _ = services.AddSingleton<ProjectionStorage>();
        _ = services.AddSingleton<IEventStore, InMemoryEventStore>();
        _ = services.AddSingleton<IDomainEventReader>(provider => provider.GetRequiredService<IEventStore>());
        _ = services.AddSingleton<IProjectionCheckpointStore, InMemoryProjectionCheckpointStore>();
        if (scoped)
        {
            _ = services.AddScoped<IConsumerScope, ConsumerScope>();
            _ = services.AddScoped<IProjectionTarget<IAccountProjection>, ProjectionTarget>();
        }
        else
        {
            _ = services.AddSingleton<IConsumerScope, ConsumerScope>();
            _ = services.AddSingleton<IProjectionTarget<IAccountProjection>, ProjectionTarget>();
        }
        return services;
    }

    public static ServiceProvider Build(IServiceCollection services) => services.BuildServiceProvider(
        new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

    public static ValueTask SeedAsync(IServiceProvider provider, Uuid id, ulong position = 0)
        => provider.GetRequiredService<IEventStore>().AppendAsync(new EventStreamAddress("consumer", "accounts", id.ToString()),
            position, [DomainEventSeed.Attach(new Deposited(1), id, position + 1)]);

    public sealed record Effect(string Component, Uuid AggregateId, int Amount, Guid ScopeId);

    public sealed class Effects : IConsumerEffects
    {
        readonly Lock _gate = new();
        readonly List<Effect> _items = [];
        TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConcurrentDictionary<Guid, bool> Scopes { get; } = new();

        public IReadOnlyList<Effect> Items { get { lock (_gate) return [.. _items]; } }

        public void Record(string component, Uuid aggregateId, int amount, Guid scopeId)
        {
            lock (_gate)
            {
                _items.Add(new Effect(component, aggregateId, amount, scopeId));
                var changed = _changed;
                _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                changed.SetResult();
            }
        }

        public async Task WaitForAsync(string component, int count = 1, Uuid? aggregateId = null)
        {
            while (true)
            {
                Task changed;
                lock (_gate)
                {
                    if (_items.Count(item => item.Component == component && (aggregateId is null || item.AggregateId == aggregateId)) >= count)
                        return;
                    changed = _changed.Task;
                }
                await changed.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }

    sealed class ConsumerScope : IConsumerScope, IDisposable
    {
        readonly Effects _effects;

        public ConsumerScope(Effects effects)
        {
            _effects = effects;
            effects.Scopes[Id] = false;
        }

        public Guid Id { get; } = Guid.NewGuid();

        public void Dispose() => _effects.Scopes[Id] = true;
    }

    public sealed class ProjectionStorage
    {
        public ConcurrentDictionary<string, ProjectionCheckpoint> Checkpoints { get; } = new();
        public bool FailAfterCommit { get; set; }
        public bool FailReload { get; set; }
        public int LoadAttempts { get; set; }
    }

    sealed class ProjectionTarget(ProjectionStorage storage, IConsumerEffects effects) : IProjectionTarget<IAccountProjection>
    {
        public ValueTask<ProjectionCheckpoint> LoadCheckpointAsync(string projectorName, CancellationToken ct = default)
        {
            storage.LoadAttempts++;
            if (storage.FailReload)
            {
                storage.FailReload = false;
                throw new InvalidOperationException("Checkpoint reload failed");
            }
            return ValueTask.FromResult(storage.Checkpoints.GetValueOrDefault(projectorName, ProjectionCheckpoint.Start));
        }

        public ValueTask<IProjectionBatch<IAccountProjection>> BeginAsync(ProjectionBatchContext context, CancellationToken ct = default)
            => ValueTask.FromResult<IProjectionBatch<IAccountProjection>>(new Batch(context.ProjectorName, storage, effects));
    }

    sealed class Batch(string name, ProjectionStorage storage, IConsumerEffects effects) : IProjectionBatch<IAccountProjection>, IAccountProjection
    {
        readonly List<(Uuid Id, int Amount, Guid ScopeId)> _pending = [];

        public IAccountProjection Projection => this;

        public void Add(Uuid aggregateId, int amount, Guid scopeId) => _pending.Add((aggregateId, amount, scopeId));

        public ValueTask CommitAsync(ProjectionCheckpoint checkpoint, CancellationToken ct = default)
        {
            foreach (var (id, amount, scopeId) in _pending)
                effects.Record(name, id, amount, scopeId);
            storage.Checkpoints[name] = checkpoint;
            if (storage.FailAfterCommit)
            {
                storage.FailAfterCommit = false;
                storage.FailReload = true;
                throw new InvalidOperationException("Commit completed but response failed");
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
