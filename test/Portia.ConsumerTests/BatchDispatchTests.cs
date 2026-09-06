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
            public record Root(int Amount) : DomainEvent;
            public sealed record Child(int Amount) : Root(Amount);
            public sealed class Repository : IProjectionStore, IProjectionCheckpointStore
            {
                public List<string> Calls = new();
                public List<IReactorContext> Reactions = new();
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
                : BaseBatchProjector(repository, EventStreamPattern.ForPattern("batch")), IBatchProjectorHandler<Root>, IBatchProjectorHandler<Child>
            {
                public ValueTask HandleAsync(IReadOnlyList<Root> events, IProjectorContext context, CancellationToken ct)
                {
                    if (!context.IsRebuild) throw new Exception("Lost rebuild context");
                    repository.Calls.Add("root:" + string.Join(",", events.Select(e => e.Amount)));
                    return ValueTask.CompletedTask;
                }
                public ValueTask HandleAsync(IReadOnlyList<Child> events, IProjectorContext context, CancellationToken ct)
                {
                    repository.Calls.Add("child:" + string.Join(",", events.Select(e => e.Amount)));
                    return ValueTask.CompletedTask;
                }
            }
            public sealed partial class Reaction(Repository repository)
                : BaseBatchReactor(repository, EventStreamPattern.ForPattern("batch")), IBatchReactorHandler<Root>, IBatchReactorHandler<Child>
            {
                public ValueTask HandleAsync(IReadOnlyList<IReactorContext<Root>> contexts, CancellationToken ct)
                {
                    repository.Reactions.AddRange(contexts);
                    repository.Calls.Add("root:" + string.Join(",", contexts.Select(c => c.Ev.Amount)));
                    return ValueTask.CompletedTask;
                }
                public ValueTask HandleAsync(IReadOnlyList<IReactorContext<Child>> contexts, CancellationToken ct)
                {
                    repository.Reactions.AddRange(contexts);
                    repository.Calls.Add("child:" + string.Join(",", contexts.Select(c => c.Ev.Amount)));
                    return ValueTask.CompletedTask;
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
                    var repository = new Repository();
                    await new ProjectorRunner(store).RunAsync(new Projector(repository), ProjectionCheckpoint.Start, new ProjectionRunOptions { RebuildId = "new" });
                    await new ReactorRunner(store).RunAsync(new Reaction(repository), ProjectionCheckpoint.Start);
                    return string.Join("|", repository.Calls) == "root:1,2|child:3|root:4|root:1,2|child:3|root:4"
                        && repository.Reactions.Select(c => c.CauseId).SequenceEqual(events.Select(e => e.Metadata.EventId))
                        && repository.Reactions.Select(c => c.ExecutionId).Distinct().Count() == 4
                        && repository.Reactions.All(c => RequestActor.IsSystem(c.Actor));
                }
            }
            """, new ProjectorReactorEventDispatcherGenerator());
        Assert.True(await assembly.GetType("Scenario")!.GetMethod("Run")!.CreateDelegate<Func<Task<bool>>>()());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BatchHandlersRequireAnExplicitBatchBase(bool reactor)
    {
        var source = reactor
            ? "public partial class Bad(IProjectionCheckpointStore store) : BaseReactor(store, EventStreamPattern.ForPattern(\"test\")), IBatchReactorHandler<Ev> { public ValueTask HandleAsync(IReadOnlyList<IReactorContext<Ev>> contexts, CancellationToken ct) => ValueTask.CompletedTask; }"
            : "public partial class Bad(IProjectionStore store) : BaseProjector(store, EventStreamPattern.ForPattern(\"test\")), IBatchProjectorHandler<Ev> { public ValueTask HandleAsync(IReadOnlyList<Ev> events, IProjectorContext context, CancellationToken ct) => ValueTask.CompletedTask; }";
        var diagnostics = GeneratorCompilation.Diagnostics("using System.Collections.Generic; using System.Threading; using System.Threading.Tasks; using Cntryl.Portia; public sealed record Ev : DomainEvent; " + source,
            new ProjectorReactorEventDispatcherGenerator());
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "PORTIA017");
    }
}
