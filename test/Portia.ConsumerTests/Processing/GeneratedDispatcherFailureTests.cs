namespace Cntryl.Portia.Consumer;

public sealed class GeneratedDispatcherFailureTests
{
    [Theory]
    [InlineData(false, "single", "1,2,3")]
    [InlineData(true, "batch", "1,2,3,4")]
    public async Task ShouldStopNestedDispatcherGivenSingleOrBatchFailure(bool rejectBatch, string error, string observed)
    {
        var assembly = GeneratorCompilation.Compile("""
            using System;
            using System.Collections.Generic;
            using System.Globalization;
            using System.Threading;
            using System.Threading.Tasks;
            using Cntryl.Portia;
            using Cntryl.Portia.Testing;
            namespace Dispatch;
            public sealed record Fact(int Value) : DomainEvent;
            public sealed record Marker(int Value) : DomainEvent;
            public sealed class BatchMetadata : IProjectorContext
            {
                public CheckpointIdentity Identity { get; } = new("nested", EventStreamPattern.ForPattern("nested"));
                public bool IsRebuild => false;
            }
            public partial class @namespace
            {
                public sealed partial class @event(IProjectionStore store, bool rejectBatch)
                    : BatchProjector(store, EventStreamPattern.ForPattern("nested")),
                    IBatchProjectorHandler<Fact>, IProjectorHandler<Marker>
                {
                    public List<int> Observed { get; } = [];
                    public RequestError Rejection { get; } = RequestError.Conflict(rejectBatch ? "batch" : "single");
                    public ValueTask<Result> HandleAsync(IReadOnlyList<IProjectorContext<Fact>> contexts, CancellationToken ct)
                    {
                        foreach (var context in contexts) Observed.Add(context.Trigger.Value);
                        return ValueTask.FromResult(rejectBatch && contexts[0].Trigger.Value == 4
                            ? Result.Failure(Rejection) : Result.Success);
                    }
                    public ValueTask<Result> HandleAsync(IProjectorContext<Marker> context, CancellationToken ct)
                    {
                        Observed.Add(context.Trigger.Value);
                        return ValueTask.FromResult(rejectBatch ? Result.Success : Result.Failure(Rejection));
                    }
                    public ValueTask<Result> Dispatch(IReadOnlyList<DomainEventRecord> records) =>
                        ProjectBatchAsync(records, new BatchMetadata(), default);
                }
            }
            public static class Scenario
            {
                public static async Task<string[]> Run(bool rejectBatch)
                {
                    var projector = new @namespace.@event(new ProjectorScenario().Store, rejectBatch);
                    var result = await projector.Dispatch([Record(new Fact(1), 1), Record(new Fact(2), 2),
                        Record(new Marker(3), 3), Record(new Fact(4), 4), Record(new Marker(5), 5)]);
                    return [result.IsSuccess.ToString(), result.Error!.Message,
                        string.Join(",", projector.Observed), ReferenceEquals(result.Error, projector.Rejection).ToString()];
                }
                static DomainEventRecord Record(DomainEvent fact, ulong offset)
                {
                    var id = Uuid.CreateVersion4();
                    DomainEventSeed.Attach(fact, id, offset);
                    return new DomainEventRecord(new EventStreamAddress("nested", "events", id.ToString()), fact,
                        offset, new EventCursor(offset.ToString(CultureInfo.InvariantCulture)));
                }
            }
            """, new ProjectorReactorEventDispatcherGenerator());
        var run = assembly.GetType("Dispatch.Scenario")!.GetMethod("Run")!.CreateDelegate<Func<bool, Task<string[]>>>();

        var result = await run(rejectBatch);

        Assert.Equal(["False", error, observed, "True"], result);
    }
}
