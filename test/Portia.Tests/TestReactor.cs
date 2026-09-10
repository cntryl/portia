namespace Cntryl.Portia;

sealed partial class TestReactor(IAggregateRepository repository, IProjectionCheckpointStore? checkpoints = null)
    : Reactor(checkpoints ?? new InMemoryProjectionCheckpointStore(), EventStreamPattern.ForPattern("test", "reactors"),
            "test-reactor"),
        IReactorHandler<ValueChanged>,
        IReactorHandler<ValueIncremented>
{
    public int? LastIncrementAmount { get; private set; }

    public async ValueTask HandleAsync(IReactorContext<ValueChanged> context, CancellationToken ct)
    {
        var target = await repository.HydrateAsync(new TestAggregate(Uuid.CreateVersion4()), ct);
        target.ChangeValue(context.Trigger.Value);
        await repository.SaveAsync(target, context, ct);
    }

    public ValueTask HandleAsync(IReactorContext<ValueIncremented> context, CancellationToken ct)
    {
        LastIncrementAmount = context.Trigger.Amount;
        return ValueTask.CompletedTask;
    }

    public Uuid EffectId(IReactorContext context, string name) => CreateEffectId(context, name);
}
