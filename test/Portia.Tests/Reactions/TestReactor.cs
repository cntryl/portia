namespace Cntryl.Portia;

sealed partial class TestReactor(
    RecordingAggregateRepository repository,
    IProjectionCheckpointStore? checkpoints = null,
    EventStreamPattern? pattern = null)
    : Reactor(checkpoints ?? new InMemoryProjectionCheckpointStore(),
            pattern ?? EventStreamPattern.ForPattern("test", "reactors"),
            "test-reactor"),
        IReactorHandler<ValueChanged>,
        IReactorHandler<ValueIncremented>
{
    public int? LastIncrementAmount { get; private set; }

    public async ValueTask<Result> HandleAsync(IReactorContext<ValueChanged> context, CancellationToken ct)
    {
        var target = await repository.HydrateAsync(new TestAggregate(Uuid.CreateVersion4()), ct);
        target.ChangeValue(context.Trigger.Value);
        await repository.SaveAsync(target, context, ct);

        return Result.Success;
    }

    public ValueTask<Result> HandleAsync(IReactorContext<ValueIncremented> context, CancellationToken ct)
    {
        LastIncrementAmount = context.Trigger.Amount;
        return ValueTask.FromResult(Result.Success);
    }

    public Uuid EffectId(IReactorContext context, string name) => CreateEffectId(context, name);
}
