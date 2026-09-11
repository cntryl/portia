using System.Security.Claims;

namespace Cntryl.Portia;

/// <summary>
///     Verifies the guards on the typed reaction context generated dispatch hands a reactor. Effects a
///     reaction produces are attributed to the triggering event and executed as the system, and both of
///     those are read straight off this context — so a context built around the wrong event, or around a
///     user's principal, would misattribute durable writes in a way nothing downstream could detect.
/// </summary>
public sealed class ReactorContextTests
{
    /// <summary>
    ///     Verifies the shape generated dispatch actually builds: the trigger is the record's own event,
    ///     the execution is a system one caused by that event, and the context exposes both unchanged.
    /// </summary>
    [Fact]
    public void ShouldExposeTheTriggeringEventAndItsSystemExecution()
    {
        var record = Record(out var ev);
        var execution = new ReactionExecutionContext(record, RequestActor.System);

        var context = new ReactorContext<ValueChanged>(ev, record, execution);

        Assert.Same(ev, context.Trigger);
        Assert.Same(record, context.Source);
        Assert.Equal(ev.Metadata.EventId, context.CauseId);
        Assert.True(RequestActor.IsSystem(context.Actor));
    }

    /// <summary>
    ///     Verifies that a context cannot pair one record with a different event instance. The effect
    ///     identity a reaction derives is built from the source record, so a mismatched pair would
    ///     deduplicate one event's effects against another's.
    /// </summary>
    [Fact]
    public void ShouldRejectATriggerThatIsNotTheRecordsOwnEvent()
    {
        var record = Record(out _);
        var stranger = new ValueChanged(99);
        stranger.AttachMetadata(new DomainEventMetadata(Uuid.CreateVersion4(), record.Event.Metadata.AggregateId, 2,
            DateTimeOffset.UtcNow));
        var execution = new ReactionExecutionContext(record, RequestActor.System);

        var error = Assert.Throws<ArgumentException>(() =>
            new ReactorContext<ValueChanged>(stranger, record, execution));

        Assert.Contains("actual triggering event", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Verifies that an execution whose cause is some other event is refused, even when the trigger
    ///     instance itself matches — causation is what ties a reaction's writes back to what produced
    ///     them, and a context is the only place the two are checked against each other.
    /// </summary>
    [Fact]
    public void ShouldRejectAnExecutionCausedByADifferentEvent()
    {
        var record = Record(out var ev);
        var other = Record(out _);
        var execution = new ReactionExecutionContext(other, RequestActor.System);

        var error = Assert.Throws<ArgumentException>(() =>
            new ReactorContext<ValueChanged>(ev, record, execution));

        Assert.Contains("actual triggering event", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Verifies that a reaction cannot execute as a user. A reaction is not the user's action — it
    ///     is the system's response to an event the user caused — so borrowing the triggering actor's
    ///     principal would let a reaction's effects pass authorization the user never had.
    /// </summary>
    [Fact]
    public void ShouldRejectAnExecutionThatIsNotASystemPrincipal()
    {
        var record = Record(out var ev);
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "alice")], "jwt"));
        var execution = new UserExecution(record, user);

        var error = Assert.Throws<ArgumentException>(() => new ReactorContext<ValueChanged>(ev, record, execution));

        Assert.Contains("system principal", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Verifies that a missing record or execution is rejected before anything reads it.</summary>
    /// <param name="missingRecord">Whether the record is the omitted argument.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ShouldRejectAMissingRecordOrExecution(bool missingRecord)
    {
        var record = Record(out var ev);

        _ = Assert.Throws<ArgumentNullException>(() => missingRecord
            ? new ReactorContext<ValueChanged>(ev, null!, new ReactionExecutionContext(record, RequestActor.System))
            : new ReactorContext<ValueChanged>(ev, record, null!));
    }

    static DomainEventRecord Record(out ValueChanged ev)
    {
        var aggregateId = Uuid.CreateVersion4();
        ev = new ValueChanged(42);
        ev.AttachMetadata(new DomainEventMetadata(Uuid.CreateVersion4(), aggregateId, 1, DateTimeOffset.UtcNow));
        return new DomainEventRecord(new EventStreamAddress("test", "reactions", aggregateId.ToString()), ev, 0, 0, 0);
    }

    // An execution that is well formed apart from running as a user rather than the system.
    sealed class UserExecution(DomainEventRecord source, ClaimsPrincipal actor) : IExecutionContext
    {
        public Uuid ExecutionId { get; } = Uuid.CreateVersion4();
        public Uuid CorrelationId { get; } = source.Event.Metadata.EventId;
        public Uuid CauseId { get; } = source.Event.Metadata.EventId;
        public ClaimsPrincipal Actor { get; } = actor;
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    }
}
