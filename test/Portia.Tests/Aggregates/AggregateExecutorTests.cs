using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

/// <summary>
///     Verifies the aggregate executor's mechanical protocol: hydrate, invoke, persist exactly what the
///     operation chose, and return the operation's own result. Persistence and the result are independent.
/// </summary>
public sealed class AggregateExecutorTests
{
    static readonly RequestError Rejected = new(RequestErrorKind.Unauthorized, "The identity is not registered.");

    /// <summary>A successful state change the handler commits is hydrated, applied, and persisted.</summary>
    [Fact]
    public async Task ShouldHydrateInvokeAndCommitWhenOutcomeIsCommit()
    {
        // Arrange
        var store = new RecordingEventStore();
        var id = await SeedAsync(store, 3);
        var executor = Executor(store);

        // Act
        var result = await executor.ExecuteAsync(new TestAggregate(id), aggregate =>
        {
            aggregate.ChangeValue(aggregate.Value + 1);
            return AggregateOutcome.Commit(Result.Success);
        }, Context());

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(4, await ValueAsync(store, id));
    }

    /// <summary>A rejected operation can keep its audit, as a failed login does.</summary>
    [Fact]
    public async Task ShouldCommitAuditAndReturnFailureWhenFailedOutcomeIsCommit()
    {
        // Arrange
        var store = new RecordingEventStore();
        var executor = Executor(store);

        // Act
        var result = await executor.ExecuteAsync(new TestAggregate(Uuid.CreateVersion4()), aggregate =>
        {
            aggregate.Audit("login-failed");
            return AggregateOutcome.Commit(Result.Failure(Rejected));
        }, Context());

        // Assert
        Assert.Same(Rejected, result.Error);
        Assert.Equal(1, store.Appends);
        Assert.IsType<ValueAudited>(Assert.Single(store.Appended));
    }

    /// <summary>A rejected operation can still commit the state it changed.</summary>
    [Fact]
    public async Task ShouldCommitStateAndReturnFailureWhenFailedOutcomeIsCommit()
    {
        // Arrange
        var store = new RecordingEventStore();
        var id = Uuid.CreateVersion4();
        var executor = Executor(store);

        // Act
        var result = await executor.ExecuteAsync(new TestAggregate(id), aggregate =>
        {
            aggregate.ChangeValue(9);
            return AggregateOutcome.Commit(Result.Failure(new RequestError(RequestErrorKind.Conflict, "locked")));
        }, Context());

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(RequestErrorKind.Conflict, result.Error.Kind);
        Assert.Equal(9, await ValueAsync(store, id));
    }

    /// <summary>Several aggregate calls in one operation share one commit.</summary>
    [Fact]
    public async Task ShouldCommitEveryChangeFromOneOperationTogether()
    {
        // Arrange
        var store = new RecordingEventStore();
        var id = Uuid.CreateVersion4();
        var executor = Executor(store);

        // Act
        var result = await executor.ExecuteAsync(new TestAggregate(id), aggregate =>
        {
            aggregate.ChangeValue(1);
            aggregate.ChangeValue(aggregate.Value + 1);
            return AggregateOutcome.Commit(Result.Success);
        }, Context());

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(1, store.Appends);
        Assert.Equal(2, store.Appended.Count);
        Assert.Equal(2, await ValueAsync(store, id));
    }

    /// <summary>A discarded outcome writes nothing, whatever the aggregate recorded.</summary>
    [Fact]
    public async Task ShouldNotWriteWhenOutcomeIsDiscard()
    {
        // Arrange
        var store = new RecordingEventStore();
        var id = Uuid.CreateVersion4();
        var executor = Executor(store);

        // Act
        var result = await executor.ExecuteAsync(new TestAggregate(id), aggregate =>
        {
            aggregate.ChangeValue(5);
            return AggregateOutcome.Discard(Result.Failure(Rejected));
        }, Context());

        // Assert
        Assert.Same(Rejected, result.Error);
        Assert.Equal(0, store.Appends);
        Assert.Equal(0, await ValueAsync(store, id));
    }

    /// <summary>Committing when the aggregate recorded nothing is a harmless no-op.</summary>
    [Fact]
    public async Task ShouldTreatCommitWithNothingPendingAsNoOp()
    {
        // Arrange
        var store = new RecordingEventStore();
        var executor = Executor(store);

        // Act
        var result = await executor.ExecuteAsync(new TestAggregate(Uuid.CreateVersion4()),
            _ => AggregateOutcome.Commit(Result.Success), Context());

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(0, store.Appends);
    }

    /// <summary>A typed operation returns its value.</summary>
    [Fact]
    public async Task ShouldReturnTypedValue()
    {
        // Arrange
        var store = new RecordingEventStore();
        var id = await SeedAsync(store, 11);
        var executor = Executor(store);

        // Act
        var result = await executor.ExecuteAsync(new TestAggregate(id),
            aggregate => AggregateOutcome.Discard(Result<int>.Success(aggregate.Value)), Context());

        // Assert
        Assert.Equal(11, result.Value);
    }

    /// <summary>Saved events are attributed to the explicitly supplied execution context.</summary>
    [Fact]
    public async Task ShouldAttributeSavedEventsFromExplicitContext()
    {
        // Arrange
        var store = new RecordingEventStore();
        var executor = Executor(store);
        var context = Context();

        // Act
        _ = await executor.ExecuteAsync(new TestAggregate(Uuid.CreateVersion4()), aggregate =>
        {
            aggregate.ChangeValue(1);
            return AggregateOutcome.Commit(Result.Success);
        }, context);

        // Assert
        var metadata = Assert.Single(store.Appended).Metadata;
        Assert.Equal(context.ExecutionId, metadata.ExecutionId);
        Assert.Equal(context.CorrelationId, metadata.CorrelationId);
    }

    /// <summary>An exception from the operation propagates and nothing is saved.</summary>
    [Fact]
    public async Task ShouldPropagateOperationExceptionWithoutSaving()
    {
        // Arrange
        var store = new RecordingEventStore();

        // Act
        var executor = Executor(store);

        // Assert
        _ = await Assert.ThrowsAsync<DivideByZeroException>(() => executor.ExecuteAsync(
            new TestAggregate(Uuid.CreateVersion4()), aggregate =>
            {
                aggregate.ChangeValue(1);
                throw new DivideByZeroException();
#pragma warning disable CS0162
                return AggregateOutcome.Commit(Result.Success);
#pragma warning restore CS0162
            }, Context()).AsTask());

        Assert.Equal(0, store.Appends);
    }

    /// <summary>A concurrency conflict propagates after one attempt; the executor never retries.</summary>
    [Fact]
    public async Task ShouldPropagateConcurrencyExceptionWithoutRetrying()
    {
        // Arrange
        var store = new RecordingEventStore { Conflict = true };

        // Act
        var executor = Executor(store);

        // Assert
        _ = await Assert.ThrowsAsync<EventStreamConcurrencyException>(() => executor.ExecuteAsync(
            new TestAggregate(Uuid.CreateVersion4()), aggregate =>
            {
                aggregate.ChangeValue(1);
                return AggregateOutcome.Commit(Result.Success);
            }, Context()).AsTask());

        Assert.Equal(1, store.Appends);
    }

    /// <summary>An operation returning an uninitialized outcome is rejected before anything is written.</summary>
    [Fact]
    public async Task ShouldRejectDefaultOutcome()
    {
        // Arrange
        var store = new RecordingEventStore();

        // Act
        var executor = Executor(store);

        // Assert
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(
            new TestAggregate(Uuid.CreateVersion4()), aggregate =>
            {
                aggregate.ChangeValue(1);
                return default;
            }, Context()).AsTask());

        Assert.Contains(nameof(AggregateOutcome), error.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(TestAggregate), error.Message, StringComparison.Ordinal);
        Assert.Equal(0, store.Appends);
    }

    /// <summary>An outcome cannot be built from an uninitialized result.</summary>
    [Fact]
    public void ShouldRejectUninitializedResultInOutcome()
    {
        // Arrange
        // Act
        var commit = Record.Exception(() => AggregateOutcome.Commit(default));
        var discard = Record.Exception(() => AggregateOutcome.Discard(default(Result<int>)));

        // Assert
        _ = Assert.IsType<ArgumentException>(commit);
        _ = Assert.IsType<ArgumentException>(discard);
    }

    /// <summary>The common policy commits a successful untyped result and discards a failed one.</summary>
    [Fact]
    public void ShouldCommitUntypedResultOnlyWhenSuccessful()
    {
        // Arrange
        var success = AggregateOutcome.CommitOnSuccess(Result.Success);

        // Act
        var failure = AggregateOutcome.CommitOnSuccess(Result.Failure(Rejected));

        // Assert
        Assert.Equal(AggregateDisposition.Commit, success.Disposition);
        Assert.True(success.Result.IsSuccess);
        Assert.Equal(AggregateDisposition.Discard, failure.Disposition);
        Assert.Same(Rejected, failure.Result.Error);
    }

    /// <summary>The common policy preserves typed values and errors while choosing the disposition.</summary>
    [Fact]
    public void ShouldCommitTypedResultOnlyWhenSuccessful()
    {
        // Arrange
        var success = AggregateOutcome.CommitOnSuccess(Result<int>.Success(42));

        // Act
        var failure = AggregateOutcome.CommitOnSuccess(Result<int>.Failure(Rejected));

        // Assert
        Assert.Equal(AggregateDisposition.Commit, success.Disposition);
        Assert.Equal(42, success.Result.Value);
        Assert.Equal(AggregateDisposition.Discard, failure.Disposition);
        Assert.Same(Rejected, failure.Result.Error);
    }

    /// <summary>The convenience policy rejects default results just like the explicit factories.</summary>
    [Fact]
    public void ShouldRejectUninitializedResultInCommitOnSuccess()
    {
        // Arrange
        // Act
        var untyped = Record.Exception(() => AggregateOutcome.CommitOnSuccess(default));
        var typed = Record.Exception(() => AggregateOutcome.CommitOnSuccess(default(Result<int>)));

        // Assert
        _ = Assert.IsType<ArgumentException>(untyped);
        _ = Assert.IsType<ArgumentException>(typed);
    }

    /// <summary>
    ///     Discarding clears the instance's pending records and invalidates it: its in-memory state no longer
    ///     matches the store, so any later use is refused.
    /// </summary>
    [Fact]
    public async Task ShouldClearAndInvalidateDiscardedAggregateWithPendingRecords()
    {
        // Arrange
        var store = new RecordingEventStore();
        var executor = Executor(store);
        var aggregate = new TestAggregate(Uuid.CreateVersion4());

        // Act
        _ = await executor.ExecuteAsync(aggregate, current =>
        {
            current.ChangeValue(5);
            return AggregateOutcome.Discard(Result.Success);
        }, Context());

        // Assert
        Assert.Empty(aggregate.UncommittedEvents);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(
            aggregate, _ => AggregateOutcome.Commit(Result.Success), Context()).AsTask());

        Assert.Contains("discarded", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, store.Appends);
    }

    /// <summary>
    ///     An operation that throws commits nothing: what it raised is discarded and the instance is invalidated,
    ///     so a caller that reuses it cannot save a failed operation's events later.
    /// </summary>
    [Fact]
    public async Task ShouldDiscardAndInvalidateWhenOperationThrows()
    {
        // Arrange
        var store = new RecordingEventStore();
        var executor = Executor(store);

        // Act
        var aggregate = new TestAggregate(Uuid.CreateVersion4());

        // Assert
        _ = await Assert.ThrowsAsync<DivideByZeroException>(() => executor.ExecuteAsync(aggregate, current =>
        {
            current.ChangeValue(5);
            throw new DivideByZeroException();
        }, Context()).AsTask());

        Assert.Empty(aggregate.UncommittedEvents);
        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(aggregate, current =>
        {
            current.ChangeValue(6);
            return AggregateOutcome.Commit(Result.Success);
        }, Context()).AsTask());
        Assert.Equal(0, store.Appends);
    }

    /// <summary>
    ///     A handler that throws part-way through applying a raised event leaves state that nothing recorded,
    ///     so the instance is refused afterwards even though no event was pending to discard.
    /// </summary>
    [Fact]
    public async Task ShouldInvalidateWhenAnEventHandlerThrowsPartWay()
    {
        // Arrange
        var store = new RecordingEventStore();
        var executor = Executor(store);

        // Act
        var aggregate = new HalfApplyingAggregate(Uuid.CreateVersion4());

        // Assert
        _ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => executor.ExecuteAsync(aggregate, current =>
        {
            current.ChangeValue(-1);
            return AggregateOutcome.Commit(Result.Success);
        }, Context()).AsTask());

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(aggregate, current =>
        {
            current.ChangeValue(6);
            return AggregateOutcome.Commit(Result.Success);
        }, Context()).AsTask());
        Assert.Equal(0, store.Appends);
    }

    /// <summary>The value-returning overload discards a throwing operation's records the same way.</summary>
    [Fact]
    public async Task ShouldDiscardWhenValueOperationThrows()
    {
        // Arrange
        var store = new RecordingEventStore();
        var executor = Executor(store);

        // Act
        var aggregate = new TestAggregate(Uuid.CreateVersion4());

        // Assert
        _ = await Assert.ThrowsAsync<DivideByZeroException>(() => executor.ExecuteAsync<TestAggregate, int>(
            aggregate, current =>
            {
                current.Audit("attempted");
                throw new DivideByZeroException();
            }, Context()).AsTask());

        Assert.Empty(aggregate.UncommittedAudits);
        Assert.Equal(0, store.Appends);
    }

    /// <summary>A discarded outcome with nothing recorded leaves the instance usable.</summary>
    [Fact]
    public async Task ShouldKeepAggregateUsableAfterDiscardWithNothingPending()
    {
        // Arrange
        var store = new RecordingEventStore();
        var id = await SeedAsync(store, 2);
        var executor = Executor(store);
        var aggregate = new TestAggregate(id);
        _ = await executor.ExecuteAsync(aggregate, _ => AggregateOutcome.Discard(Result.Success), Context());

        // Act
        var result = await executor.ExecuteAsync(aggregate,
            current => AggregateOutcome.Discard(Result<int>.Success(current.Value)), Context());

        // Assert
        Assert.Equal(2, result.Value);
    }

    /// <summary>A canceled token stops execution before the operation runs.</summary>
    [Fact]
    public async Task ShouldHonorCancellation()
    {
        // Arrange
        var store = new RecordingEventStore();
        var executor = Executor(store);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        // Act
        var invoked = false;

        // Assert
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executor.ExecuteAsync(
            new TestAggregate(Uuid.CreateVersion4()), _ =>
            {
                invoked = true;
                return AggregateOutcome.Commit(Result.Success);
            }, Context(), cancellation.Token).AsTask());

        Assert.False(invoked);
    }

    /// <summary>The aggregate rule that events and audits cannot share a batch surfaces unchanged.</summary>
    [Fact]
    public async Task ShouldSurfaceAggregateBatchRuleUnchanged()
    {
        // Arrange
        var store = new RecordingEventStore();

        // Act
        var executor = Executor(store);

        // Assert
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(
            new TestAggregate(Uuid.CreateVersion4()), aggregate =>
            {
                aggregate.ChangeValue(1);
                aggregate.Audit("after-change");
                return AggregateOutcome.Commit(Result.Success);
            }, Context()).AsTask());

        Assert.Contains("before recording audits", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, store.Appends);
    }

    /// <summary>AddPortia registers one executor per scope.</summary>
    [Fact]
    public async Task ShouldResolveScopedAggregateExecutor()
    {
        // Arrange
        var services = new ServiceCollection();
        _ = services.AddSingleton<IEventStore>(new RecordingEventStore());
        _ = services.AddPortia();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var first = provider.CreateAsyncScope();
        await using var second = provider.CreateAsyncScope();

        // Act
        var executor = first.ServiceProvider.GetRequiredService<IAggregateExecutor>();

        // Assert
        Assert.Same(executor, first.ServiceProvider.GetRequiredService<IAggregateExecutor>());
        Assert.NotSame(executor, second.ServiceProvider.GetRequiredService<IAggregateExecutor>());
    }

    static AggregateExecutor Executor(IEventStore store)
    {
        var repository = new AggregateRepository(store);
        return new AggregateExecutor(repository, repository);
    }

    static RequestDispatchContext Context() => new(RequestActor.System);

    static async Task<Uuid> SeedAsync(IEventStore store, int value)
    {
        var id = Uuid.CreateVersion4();
        var aggregate = new TestAggregate(id);
        aggregate.ChangeValue(value);
        await new AggregateRepository(store).SaveAsync(aggregate, Context());
        if (store is RecordingEventStore recording)
            recording.Reset();
        return id;
    }

    static async Task<int> ValueAsync(IEventStore store, Uuid id) =>
        (await new AggregateRepository(store).HydrateAsync(new TestAggregate(id))).Value;

    sealed class RecordingEventStore : IEventStore
    {
        readonly InMemoryEventStore _inner = new();

        public bool Conflict { get; init; }
        public int Appends { get; private set; }
        public List<DomainEvent> Appended { get; } = [];

        public IAsyncEnumerable<DomainEventRecord> ReadAsync(EventStreamAddress stream, ulong fromOffset,
            CancellationToken ct) => _inner.ReadAsync(stream, fromOffset, ct);

        public IAsyncEnumerable<DomainEventRecord> ReadAsync(EventStreamPattern pattern, EventCursor cursor,
            CancellationToken ct) => _inner.ReadAsync(pattern, cursor, ct);

        public ValueTask AppendAsync(EventStreamAddress stream, ulong expectedStreamPosition,
            IReadOnlyList<DomainEvent> events, CancellationToken ct = default)
        {
            Appends++;
            if (Conflict)
                throw new EventStreamConcurrencyException("The stream moved on.");
            Appended.AddRange(events);
            return _inner.AppendAsync(stream, expectedStreamPosition, events, ct);
        }

        public void Reset()
        {
            Appends = 0;
            Appended.Clear();
        }
    }

    sealed class HalfApplyingAggregate : Aggregate
    {
        public HalfApplyingAggregate(Uuid id) : base(id, new EventStreamAddress("test", "aggregates", id.ToString()))
        {
            On<ValueChanged>(ev =>
            {
                Value = ev.Value;
                ArgumentOutOfRangeException.ThrowIfNegative(ev.Value);
            });
        }

        public int Value { get; private set; }

        public void ChangeValue(int value) => RaiseEvent(new ValueChanged(value));
    }
}
