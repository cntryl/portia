namespace Cntryl.Portia.Consumer;

public sealed class ComponentCompilationTests
{
    [Theory]
    [InlineData(false, false, "class")]
    [InlineData(true, false, "class")]
    [InlineData(true, false, "interface")]
    [InlineData(false, true, "class")]
    public async Task GlobalNestedAndPartialComponentsCompileAndExecute(bool nested, bool partial, string containerKind)
    {
        var components = """
            public partial class Watcher : Reactor, IReactorHandler<Ev>
            {
                public Watcher() : base(new InMemoryProjectionCheckpointStore(), EventStreamPattern.ForPattern("test"), "watcher") { }
                public int Count { get; private set; }
                public ValueTask HandleAsync(IReactorContext<Ev> context, CancellationToken ct) { Count++; return ValueTask.CompletedTask; }
                public ValueTask Process()
                {
                    var id = Uuid.CreateVersion4();
                    var ev = new Ev();
                    ev.AttachMetadata(new DomainEventMetadata(Uuid.CreateVersion4(), id, 1, DateTimeOffset.UtcNow));
                    var record = new DomainEventRecord(new EventStreamAddress("test", "area", id.ToString()), ev, 0, 0, 0);
                    return ReactToEventAsync(record, new ReactionExecutionContext(record, RequestActor.System), default);
                }
            }
            public partial class View : Projector, IProjectorHandler<Ev>
            {
                public View() : base(new Target(), EventStreamPattern.ForPattern("test"), "view") { }
                public int Count { get; private set; }
                public ValueTask HandleAsync(Ev ev, IProjectorContext context, CancellationToken ct) { Count++; return ValueTask.CompletedTask; }
                public ValueTask Process() => ProjectEventAsync(new DomainEventRecord(new EventStreamAddress("test", "area", "id"), new Ev(), 0, 0, 0), new Context(), default);
            }
            """;
        if (partial)
            components += "public partial class Watcher { } public partial class View { }";
        if (nested)
            components = "public partial " + containerKind + " Container { " + components + " }";
        var prefix = nested ? "Container." : "";
        var assembly = GeneratorCompilation.Compile("""
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Cntryl.Portia;
            using Cntryl.Portia.Testing;
            public sealed record Ev : DomainEvent;
            public sealed class Context : IProjectorContext
            {
                public CheckpointIdentity Identity { get; } = new("test", EventStreamPattern.ForPattern("test"));
                public bool IsRebuild => false;
            }
            public sealed class Target : IProjectionStore
            {
                public ValueTask<ProjectionCheckpoint> LoadCheckpointAsync(CheckpointIdentity name, CancellationToken ct = default) => throw new NotSupportedException();
                public ValueTask<IProjectionBatch> BeginAsync(ProjectionBatchContext context, CancellationToken ct = default) => throw new NotSupportedException();
            }
            """ + components + $$"""
            public static class Scenario
            {
                public static async Task<int> Run()
                {
                    var watcher = new {{prefix}}Watcher();
                    var view = new {{prefix}}View();
                    await watcher.Process();
                    await view.Process();
                    return watcher.Count + view.Count;
                }
            }
            """, new ProjectorReactorEventDispatcherGenerator());
        var run = assembly.GetType("Scenario")!.GetMethod("Run")!.CreateDelegate<Func<Task<int>>>();
        Assert.Equal(2, await run());
    }
}
