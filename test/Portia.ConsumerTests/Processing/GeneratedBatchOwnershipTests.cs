namespace Cntryl.Portia.Consumer;

public sealed class GeneratedBatchOwnershipTests
{
    [Fact]
    public async Task ShouldKeepRetainedBatchGivenLaterDispatch()
    {
        // Arrange
        var assembly = GeneratorCompilation.Compile("""
            using System;
            using System.Collections.Generic;
            using System.Globalization;
            using System.Threading;
            using System.Threading.Tasks;
            using Cntryl.Portia;
            using Cntryl.Portia.Testing;
            public sealed record Fact(int Value) : DomainEvent;
            public sealed class BatchMetadata : IProjectorContext
            {
                public CheckpointIdentity Identity { get; } = new("test", EventStreamPattern.ForPattern("test"));
                public bool IsRebuild => false;
            }
            public sealed partial class Projection(IProjectionStore store)
                : BatchProjector(store, EventStreamPattern.ForPattern("test")), IBatchProjectorHandler<Fact>
            {
                public IReadOnlyList<IProjectorContext<Fact>> Last { get; private set; }
                public ValueTask<Result> HandleAsync(IReadOnlyList<IProjectorContext<Fact>> contexts, CancellationToken ct)
                {
                    Last = contexts;
                    return ValueTask.FromResult(Result.Success);
                }
                public ValueTask<Result> Run(IReadOnlyList<DomainEventRecord> records) => ProjectBatchAsync(records, new BatchMetadata(), default);
            }
            public static class Scenario
            {
                public static async Task<int[]> Run()
                {
                    var projection = new Projection(new ProjectorScenario().Store);
                    await projection.Run(new[] { Record(1), Record(2) });
                    var retained = projection.Last;
                    await projection.Run(new[] { Record(3) });
                    return new[] { retained[0].Trigger.Value, retained[1].Trigger.Value,
                        projection.Last[0].Trigger.Value, ReferenceEquals(retained, projection.Last) ? 1 : 0 };
                }
                static DomainEventRecord Record(int value)
                {
                    var id = Uuid.CreateVersion4();
                    var fact = DomainEventSeed.Attach(new Fact(value), id, (ulong)value);
                    return new DomainEventRecord(new EventStreamAddress("test", "area", id.ToString()), fact,
                        (ulong)value, new EventCursor(value.ToString(CultureInfo.InvariantCulture)));
                }
            }
            """, new ProjectorReactorEventDispatcherGenerator());
        var run = assembly.GetType("Scenario")!.GetMethod("Run")!.CreateDelegate<Func<Task<int[]>>>();

        // Act
        var values = await run();

        // Assert
        Assert.Equal([1, 2, 3, 0], values);
    }
}
