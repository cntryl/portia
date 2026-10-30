using System.Text.Json.Serialization;

namespace Cntryl.Portia.Consumer;

public static partial class ProcessorOutcomeFixture
{
    [Discriminator("processor-outcome.changed")]
    public sealed record Changed(int Value) : DomainEvent;

    public sealed class State : IProjectionStore, IProjectionCheckpointStore
    {
        int _pending;
        public bool Projection { get; set; } = true;
        public RequestError? Error { get; set; }
        public Exception? Exception { get; set; }
        public Exception? DisposeException { get; set; }
        public bool ReturnDefault { get; set; }
        public ProjectionCheckpoint Checkpoint { get; set; }
        public int Value { get; private set; }
        public int Loads { get; private set; }
        public int Disposals { get; private set; }
        public List<int> Seen { get; } = [];
        public List<IProjectorContext<Changed>> Contexts { get; } = [];

        public Result Apply(IProjectorContext<Changed> context)
        {
            Contexts.Add(context);
            return Apply(context.Trigger.Value);
        }

        public Result Apply(int value)
        {
            Seen.Add(value);
            if (Projection)
                _pending += value;
            else
                Value += value;
            if (value != 2)
                return Result.Success;
            if (Exception is not null)
                throw Exception;
            if (ReturnDefault)
                return default;
            return Error is null ? Result.Success : Result.Failure(Error);
        }

        public ValueTask<ProjectionCheckpoint> LoadCheckpointAsync(CheckpointIdentity identity, CancellationToken ct = default)
        {
            Loads++;
            return ValueTask.FromResult(Checkpoint);
        }
        public ValueTask<ProjectionCheckpoint> LoadAsync(CheckpointIdentity identity, CancellationToken ct = default) => LoadCheckpointAsync(identity, ct);
        public ValueTask<IProjectionBatch> BeginAsync(ProjectionBatchContext context, CancellationToken ct = default)
        {
            _pending = 0;
            return ValueTask.FromResult<IProjectionBatch>(new Batch(this));
        }
        public ValueTask SaveAsync(CheckpointIdentity identity, ProjectionCheckpoint checkpoint, CancellationToken ct = default)
        {
            if (Projection)
                Value += _pending;
            _pending = 0;
            Checkpoint = checkpoint;
            return ValueTask.CompletedTask;
        }
        sealed class Batch(State state) : IProjectionBatch
        {
            public ValueTask CommitAsync(ProjectionCheckpoint checkpoint, CancellationToken ct = default) =>
                state.SaveAsync(null!, checkpoint, ct);
            public ValueTask DisposeAsync()
            {
                state.Disposals++;
                state._pending = 0;
                if (state.DisposeException is not null)
                    throw state.DisposeException;
                return ValueTask.CompletedTask;
            }
        }
    }

    public sealed partial class SingleProjection(State state) : Projector(state, EventStreamPattern.ForPattern("outcome-source"), "outcomes"), IProjectorHandler<Changed>
    {
        public ValueTask<Result> HandleAsync(IProjectorContext<Changed> context, CancellationToken ct) => ValueTask.FromResult(state.Apply(context));
    }
    public sealed partial class GroupedProjection(State state) : BatchProjector(state, EventStreamPattern.ForPattern("outcome-source"), "outcomes"), IProjectorHandler<Changed>
    {
        public ValueTask<Result> HandleAsync(IProjectorContext<Changed> context, CancellationToken ct) => ValueTask.FromResult(state.Apply(context));
    }
    public sealed partial class BulkProjection(State state) : BatchProjector(state, EventStreamPattern.ForPattern("outcome-source"), "outcomes"), IBatchProjectorHandler<Changed>
    {
        public ValueTask<Result> HandleAsync(IReadOnlyList<IProjectorContext<Changed>> contexts, CancellationToken ct)
        {
            foreach (var context in contexts)
            {
                var result = state.Apply(context);
                if (!result.IsSuccess)
                    return ValueTask.FromResult(result);
            }
            return ValueTask.FromResult(Result.Success);
        }
    }
    public sealed partial class SingleReaction(State state) : Reactor(state, EventStreamPattern.ForPattern("outcome-source"), "outcomes"), IReactorHandler<Changed>
    {
        public ValueTask<Result> HandleAsync(IReactorContext<Changed> context, CancellationToken ct) => ValueTask.FromResult(state.Apply(context.Trigger.Value));
    }
    public sealed partial class GroupedReaction(State state) : BatchReactor(state, EventStreamPattern.ForPattern("outcome-source"), "outcomes"), IReactorHandler<Changed>
    {
        public ValueTask<Result> HandleAsync(IReactorContext<Changed> context, CancellationToken ct) => ValueTask.FromResult(state.Apply(context.Trigger.Value));
    }
    public sealed partial class BulkReaction(State state) : BatchReactor(state, EventStreamPattern.ForPattern("outcome-source"), "outcomes"), IBatchReactorHandler<Changed>
    {
        public ValueTask<Result> HandleAsync(IReadOnlyList<IReactorContext<Changed>> contexts, CancellationToken ct)
        {
            foreach (var context in contexts)
            {
                var result = state.Apply(context.Trigger.Value);
                if (!result.IsSuccess)
                    return ValueTask.FromResult(result);
            }
            return ValueTask.FromResult(Result.Success);
        }
    }

}

[PortiaJsonContext]
[JsonSerializable(typeof(ProcessorOutcomeFixture.Changed))]
sealed partial class ProcessorOutcomeJson : JsonSerializerContext;
