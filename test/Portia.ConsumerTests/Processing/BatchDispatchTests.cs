using System.Reflection;

namespace Cntryl.Portia.Consumer;

public sealed class BatchDispatchTests
{
    [Fact]
    public async Task GeneratedBatchesPreserveOrderAndDerivedHandlersAndCausalContexts()
    {
        var assembly = GeneratorCompilation.Compile("""
                                                    using System;
                                                    using System.Collections.Generic;
                                                    using System.Linq;
                                                    using System.Threading;
                                                    using System.Threading.Tasks;
                                                    using Cntryl.Portia;
                                                    using Cntryl.Portia.Testing;
                                                    public record Root(int Amount) : DomainEvent;
                                                    public sealed record Child(int Amount) : Root(Amount);
                                                    public sealed class Repository : IProjectionStore, IProjectionCheckpointStore
                                                    {
                                                        public List<string> Calls = new();
                                                        public List<IReactorContext> Reactions = new();
                                                        public List<IProjectorContext<Root>> Projections = new();
                                                        public DomainEvent[] Events = Array.Empty<DomainEvent>();
                                                        public CancellationToken Token;
                                                        public ValueTask<ProjectionCheckpoint> LoadCheckpointAsync(CheckpointIdentity id, CancellationToken ct = default) => ValueTask.FromResult(ProjectionCheckpoint.Start);
                                                        public ValueTask<ProjectionCheckpoint> LoadAsync(CheckpointIdentity id, CancellationToken ct = default) => ValueTask.FromResult(ProjectionCheckpoint.Start);
                                                        public ValueTask SaveAsync(CheckpointIdentity id, ProjectionCheckpoint checkpoint, CancellationToken ct = default) => ValueTask.CompletedTask;
                                                        public ValueTask<IProjectionBatch> BeginAsync(ProjectionBatchContext context, CancellationToken ct = default) => ValueTask.FromResult<IProjectionBatch>(new Batch());
                                                        public sealed class Batch : IProjectionBatch
                                                        {
                                                            public ValueTask CommitAsync(ProjectionCheckpoint checkpoint, CancellationToken ct = default) => ValueTask.CompletedTask;
                                                            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
                                                        }
                                                    }
                                                    public sealed partial class Projector(Repository repository)
                                                        : BatchProjector(repository, EventStreamPattern.ForPattern("batch")), IBatchProjectorHandler<Root>, IBatchProjectorHandler<Child>
                                                    {
                                                        public ValueTask<Result> HandleAsync(IReadOnlyList<IProjectorContext<Root>> contexts, CancellationToken ct)
                                                        {
                                                            if (contexts.Any(c => !c.IsRebuild) || ct != repository.Token
                                                                || contexts.Any(c => !ReferenceEquals(c.Trigger, c.Source.Event)
                                                                    || !ReferenceEquals(repository.Events[(int)c.Source.ResourceOffset], c.Trigger)
                                                                    || c.Source.Stream.Realm != "batch"
                                                                    || c.Source.NextCursor == EventCursor.Start))
                                                                throw new Exception("Lost projector event, context, or cancellation token");
                                                            repository.Projections.AddRange(contexts);
                                                            repository.Calls.Add("root:" + string.Join(",", contexts.Select(c => c.Trigger.Amount)));
                                                            return ValueTask.FromResult(Result.Success);
                                                        }
                                                        public ValueTask<Result> HandleAsync(IReadOnlyList<IProjectorContext<Child>> contexts, CancellationToken ct)
                                                        {
                                                            if (ct != repository.Token || contexts.Any(c => !ReferenceEquals(c.Trigger, c.Source.Event)
                                                                    || !ReferenceEquals(repository.Events[(int)c.Source.ResourceOffset], c.Trigger)
                                                                    || c.Source.Stream.Realm != "batch"
                                                                    || c.Source.NextCursor == EventCursor.Start))
                                                                throw new Exception("Lost projector event or cancellation token");
                                                            repository.Projections.AddRange(contexts);
                                                            repository.Calls.Add("child:" + string.Join(",", contexts.Select(c => c.Trigger.Amount)));
                                                            return ValueTask.FromResult(Result.Success);
                                                        }
                                                    }
                                                    public sealed partial class Reaction(Repository repository)
                                                        : BatchReactor(repository, EventStreamPattern.ForPattern("batch")), IBatchReactorHandler<Root>, IBatchReactorHandler<Child>
                                                    {
                                                        public ValueTask<Result> HandleAsync(IReadOnlyList<IReactorContext<Root>> contexts, CancellationToken ct)
                                                        {
                                                            if (ct != repository.Token || contexts.Any(c => !ReferenceEquals(c.Trigger, c.Source.Event)))
                                                                throw new Exception("Lost reactor source or cancellation token");
                                                            repository.Reactions.AddRange(contexts);
                                                            repository.Calls.Add("root:" + string.Join(",", contexts.Select(c => c.Trigger.Amount)));
                                                            return ValueTask.FromResult(Result.Success);
                                                        }
                                                        public ValueTask<Result> HandleAsync(IReadOnlyList<IReactorContext<Child>> contexts, CancellationToken ct)
                                                        {
                                                            if (ct != repository.Token || contexts.Any(c => !ReferenceEquals(c.Trigger, c.Source.Event)))
                                                                throw new Exception("Lost reactor source or cancellation token");
                                                            repository.Reactions.AddRange(contexts);
                                                            repository.Calls.Add("child:" + string.Join(",", contexts.Select(c => c.Trigger.Amount)));
                                                            return ValueTask.FromResult(Result.Success);
                                                        }
                                                    }
                                                    public static class Scenario
                                                    {
                                                        public static async Task<bool> Run()
                                                        {
                                                            var store = new InMemoryEventStore();
                                                            var id = Uuid.CreateVersion4();
                                                            DomainEvent[] events = [new Root(1), new Root(2), new Child(3), new Root(4)];
                                                            for (var i = 0; i < events.Length; i++) DomainEventSeed.Attach(events[i], id, (ulong)i + 1);
                                                            await store.AppendAsync(new EventStreamAddress("batch", "events", id.ToString()), 0, events);
                                                            using var cancellation = new CancellationTokenSource();
                                                            var repository = new Repository { Events = events, Token = cancellation.Token };
                                                            await new ProjectorRunner(store).RunAsync(new Projector(repository), ProjectionCheckpoint.Start, new ProjectionRunOptions { RebuildId = "new" }, cancellation.Token);
                                                            await new ReactorRunner(store).RunAsync(new Reaction(repository), ProjectionCheckpoint.Start, ct: cancellation.Token);
                                                            return string.Join("|", repository.Calls) == "root:1,2|child:3|root:4|root:1,2|child:3|root:4"
                                                                && repository.Projections.Count == 4
                                                                && repository.Projections.Distinct().Count() == 4
                                                                && repository.Projections.All(c => ReferenceEquals(c.Batch, repository.Projections[0].Batch))
                                                                && repository.Projections.All(c => ReferenceEquals(c.Identity, c.Batch.Identity))
                                                                && repository.Reactions.Select(c => c.CauseId).SequenceEqual(events.Select(e => e.Metadata.EventId))
                                                                && repository.Reactions.Select(c => c.ExecutionId).Distinct().Count() == 4
                                                                && repository.Reactions.All(c => RequestActor.IsSystem(c.Actor));
                                                        }
                                                    }
                                                    """, new ProjectorReactorEventDispatcherGenerator());
        Assert.True(await assembly.GetType("Scenario")!.GetMethod("Run")!.CreateDelegate<Func<Task<bool>>>()());
        Assert.Equal(["Child [batch]", "Root [batch]"],
            assembly.GetType("Projector")!.GetCustomAttribute<ProcessorTopologyAttribute>()!.Subscriptions);
        Assert.Equal(["Child [batch]", "Root [batch]"],
            assembly.GetType("Reaction")!.GetCustomAttribute<ProcessorTopologyAttribute>()!.Subscriptions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BatchHandlersRequireAnExplicitBatchBase(bool reactor)
    {
        var source = reactor
            ? "public partial class Bad(IProjectionCheckpointStore store) : Reactor(store, EventStreamPattern.ForPattern(\"test\")), IBatchReactorHandler<Ev> { public ValueTask<Result> HandleAsync(IReadOnlyList<IReactorContext<Ev>> contexts, CancellationToken ct) => ValueTask.FromResult(Result.Success); }"
            : "public partial class Bad(IProjectionStore store) : Projector(store, EventStreamPattern.ForPattern(\"test\")), IBatchProjectorHandler<Ev> { public ValueTask<Result> HandleAsync(IReadOnlyList<IProjectorContext<Ev>> contexts, CancellationToken ct) => ValueTask.FromResult(Result.Success); }";
        var diagnostics = GeneratorCompilation.Diagnostics(
            "using System.Collections.Generic; using System.Threading; using System.Threading.Tasks; using Cntryl.Portia; public sealed record Ev : DomainEvent; " +
            source,
            new ProjectorReactorEventDispatcherGenerator());
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "PORTIA017");
    }
}
