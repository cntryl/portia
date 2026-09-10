using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia.Consumer;

static class ConsumerHost
{
    public static ServiceCollection CreateServices(bool scoped = true)
    {
        var services = new ServiceCollection();
        _ = services.AddScoped<IAggregateRepository, AggregateRepository>();
        _ = services.AddSingleton<Effects>();
        _ = services.AddSingleton<IConsumerEffects>(provider => provider.GetRequiredService<Effects>());
        _ = services.AddSingleton<ProjectionStorage>();
        _ = services.AddSingleton<IEventStore, InMemoryEventStore>();
        _ = services.AddSingleton<IDomainEventReader>(provider => provider.GetRequiredService<IEventStore>());
        _ = services.AddSingleton<IProjectionCheckpointStore, InMemoryProjectionCheckpointStore>();
        if (scoped)
        {
            _ = services.AddScoped<IConsumerScope, ConsumerScope>();
            _ = services.AddScoped<IAccountRepository, AccountRepository>();
        }
        else
        {
            _ = services.AddSingleton<IConsumerScope, ConsumerScope>();
            _ = services.AddSingleton<IAccountRepository, AccountRepository>();
        }

        return services;
    }

    public static ServiceProvider Build(IServiceCollection services) => services.BuildServiceProvider(
        new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

    public static ValueTask SeedAsync(IServiceProvider provider, Uuid id, ulong position = 0)
        => provider.GetRequiredService<IEventStore>().AppendAsync(
            new EventStreamAddress("consumer", "accounts", id.ToString()),
            position, [DomainEventSeed.Attach(new Deposited(1), id, position + 1)]);

    public sealed record Effect(string Component, Uuid AggregateId, int Amount, Guid ScopeId);

    public sealed class Effects : IConsumerEffects
    {
        readonly Lock _gate = new();
        readonly List<Effect> _items = [];
        TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConcurrentDictionary<Guid, bool> Scopes { get; } = new();

        public IReadOnlyList<Effect> Items
        {
            get
            {
                lock (_gate)
                    return [.. _items];
            }
        }

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
                    if (_items.Count(item =>
                            item.Component == component && (aggregateId is null || item.AggregateId == aggregateId)) >=
                        count)
                    {
                        return;
                    }

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
        public ConcurrentDictionary<CheckpointIdentity, ProjectionCheckpoint> Checkpoints { get; } = new();
        public bool FailAfterCommit { get; set; }
        public bool FailReload { get; set; }
        public int LoadAttempts { get; set; }
    }

    sealed class AccountRepository(ProjectionStorage storage, IConsumerEffects effects)
        : IAccountRepository, IAsyncDisposable
    {
        Batch? _batch;

        public void Add(Uuid aggregateId, int amount, Guid scopeId) =>
            (_batch ?? throw new InvalidOperationException("No active batch")).Add(aggregateId, amount, scopeId);

        public ValueTask<ProjectionCheckpoint> LoadCheckpointAsync(CheckpointIdentity identity,
            CancellationToken ct = default)
        {
            storage.LoadAttempts++;
            if (storage.FailReload)
            {
                storage.FailReload = false;
                throw new InvalidOperationException("Checkpoint reload failed");
            }
            else
            {
                return ValueTask.FromResult(
                    storage.Checkpoints.GetValueOrDefault(identity, ProjectionCheckpoint.Start));
            }
        }

        public ValueTask<IProjectionBatch> BeginAsync(ProjectionBatchContext context, CancellationToken ct = default)
        {
            _batch = new Batch(context.Identity, storage, effects, () => _batch = null);
            return ValueTask.FromResult<IProjectionBatch>(_batch);
        }

        public ValueTask DisposeAsync() => _batch?.DisposeAsync() ?? ValueTask.CompletedTask;
    }

    sealed class Batch(CheckpointIdentity name, ProjectionStorage storage, IConsumerEffects effects, Action release)
        : IProjectionBatch
    {
        readonly List<(Uuid Id, int Amount, Guid ScopeId)> _pending = [];

        public ValueTask CommitAsync(ProjectionCheckpoint checkpoint, CancellationToken ct = default)
        {
            foreach (var (id, amount, scopeId) in _pending)
                effects.Record(name.ComponentName, id, amount, scopeId);
            storage.Checkpoints[name] = checkpoint;
            if (storage.FailAfterCommit)
            {
                storage.FailAfterCommit = false;
                storage.FailReload = true;
                throw new InvalidOperationException("Commit completed but response failed");
            }
            else
            {
                return ValueTask.CompletedTask;
            }
        }

        public ValueTask DisposeAsync()
        {
            release();
            return ValueTask.CompletedTask;
        }


        public void Add(Uuid aggregateId, int amount, Guid scopeId) => _pending.Add((aggregateId, amount, scopeId));
    }
}
