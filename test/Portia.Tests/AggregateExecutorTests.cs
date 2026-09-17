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
        var store = new RecordingEventStore();
        var id = await SeedAsync(store, 3);
        var executor = Executor(store);

        var result = await executor.ExecuteAsync(new TestAggregate(id), aggregate =>
        {
            aggregate.ChangeValue(aggregate.Value + 1);
            return AggregateOutcome.Commit(Result.Success);
        }, Context());

        Assert.True(result.IsSuccess);
        Assert.Equal(4, await ValueAsync(store, id));
    }

    /// <summary>A rejected operation can keep its audit, as a failed login does.</summary>
    [Fact]
    public async Task ShouldCommitAuditAndReturnFailureWhenFailedOutcomeIsCommit()
    {
        var store = new RecordingEventStore();
        var executor = Executor(store);

        var result = await executor.ExecuteAsync(new TestAggregate(Uuid.CreateVersion4()), aggregate =>
        {
            aggregate.Audit("login-failed");
            return AggregateOutcome.Commit(Result.Failure(Rejected));
        }, Context());

        Assert.Same(Rejected, result.Error);
        Assert.Equal(1, store.Appends);
        Assert.IsType<ValueAudited>(Assert.Single(store.Appended));
    }

    /// <summary>A rejected operation can still commit the state it changed.</summary>
    [Fact]
    public async Task ShouldCommitStateAndReturnFailureWhenFailedOutcomeIsCommit()
    {
        var store = new RecordingEventStore();
        var id = Uuid.CreateVersion4();
        var executor = Executor(store);

        var result = await executor.ExecuteAsync(new TestAggregate(id), aggregate =>
        {
            aggregate.ChangeValue(9);
            return AggregateOutcome.Commit(Result.Failure(new RequestError(RequestErrorKind.Conflict, "locked")));
        }, Context());

        Assert.False(result.IsSuccess);
        Assert.Equal(RequestErrorKind.Conflict, result.Error.Kind);
        Assert.Equal(9, await ValueAsync(store, id));
    }

    /// <summary>Several aggregate calls in one operation share one commit.</summary>
    [Fact]
    public async Task ShouldCommitEveryChangeFromOneOperationTogether()
    {
        var store = new RecordingEventStore();
        var id = Uuid.CreateVersion4();
        var executor = Executor(store);

        var result = await executor.ExecuteAsync(new TestAggregate(id), aggregate =>
        {
            aggregate.ChangeValue(1);
            aggregate.ChangeValue(aggregate.Value + 1);
            return AggregateOutcome.Commit(Result.Success);
        }, Context());

        Assert.True(result.IsSuccess);
        Assert.Equal(1, store.Appends);
        Assert.Equal(2, store.Appended.Count);
        Assert.Equal(2, await ValueAsync(store, id));
    }

    /// <summary>A discarded outcome writes nothing, whatever the aggregate recorded.</summary>
    [Fact]
    public async Task ShouldNotWriteWhenOutcomeIsDiscard()
    {
        var store = new RecordingEventStore();
        var id = Uuid.CreateVersion4();
        var executor = Executor(store);

        var result = await executor.ExecuteAsync(new TestAggregate(id), aggregate =>
        {
            aggregate.ChangeValue(5);
            return AggregateOutcome.Discard(Result.Failure(Rejected));
        }, Context());

        Assert.Same(Rejected, result.Error);
        Assert.Equal(0, store.Appends);
        Assert.Equal(0, await ValueAsync(store, id));
    }

    /// <summary>Committing when the aggregate recorded nothing is a harmless no-op.</summary>
    [Fact]
    public async Task ShouldTreatCommitWithNothingPendingAsNoOp()
    {
        var store = new RecordingEventStore();
        var executor = Executor(store);

        var result = await executor.ExecuteAsync(new TestAggregate(Uuid.CreateVersion4()),
            _ => AggregateOutcome.Commit(Result.Success), Context());

        Assert.True(result.IsSuccess);
        Assert.Equal(0, store.Appends);
    }

    /// <summary>A typed operation returns its value.</summary>
    [Fact]
    public async Task ShouldReturnTypedValue()
    {
        var store = new RecordingEventStore();
        var id = await SeedAsync(store, 11);
        var executor = Executor(store);

        var result = await executor.ExecuteAsync(new TestAggregate(id),
            aggregate => AggregateOutcome.Discard(Result<int>.Success(aggregate.Value)), Context());

        Assert.Equal(11, result.Value);
    }

    /// <summary>Saved events are attributed to the explicitly supplied execution context.</summary>
    [Fact]
    public async Task ShouldAttributeSavedEventsFromExplicitContext()
    {
        var store = new RecordingEventStore();
        var executor = Executor(store);
        var context = Context();

        _ = await executor.ExecuteAsync(new TestAggregate(Uuid.CreateVersion4()), aggregate =>
        {
            aggregate.ChangeValue(1);
            return AggregateOutcome.Commit(Result.Success);
        }, context);

        var metadata = Assert.Single(store.Appended).Metadata;
        Assert.Equal(context.ExecutionId, metadata.ExecutionId);
        Assert.Equal(context.CorrelationId, metadata.CorrelationId);
    }

    /// <summary>An exception from the operation propagates and nothing is saved.</summary>
    [Fact]
    public async Task ShouldPropagateOperationExceptionWithoutSaving()
    {
        var store = new RecordingEventStore();
        var executor = Executor(store);

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
        var store = new RecordingEventStore { Conflict = true };
        var executor = Executor(store);

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
        var store = new RecordingEventStore();
        var executor = Executor(store);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(
            new TestAggregate(Uuid.CreateVersion4()), aggregate =>
            {
                aggregate.ChangeValue(1);
                return default(AggregateOutcome);
            }, Context()).AsTask());

        Assert.Contains(nameof(AggregateOutcome), error.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(TestAggregate), error.Message, StringComparison.Ordinal);
        Assert.Equal(0, store.Appends);
    }

    /// <summary>An outcome cannot be built from an uninitialized result.</summary>
    [Fact]
    public void ShouldRejectUninitializedResultInOutcome()
    {
        _ = Assert.Throws<ArgumentException>(() => AggregateOutcome.Commit(default(Result)));
        _ = Assert.Throws<ArgumentException>(() => AggregateOutcome.Discard(default(Result<int>)));
    }

    /// <summary>The common policy commits a successful untyped result and discards a failed one.</summary>
    [Fact]
    public void ShouldCommitUntypedResultOnlyWhenSuccessful()
    {
        var success = AggregateOutcome.CommitOnSuccess(Result.Success);
        var failure = AggregateOutcome.CommitOnSuccess(Result.Failure(Rejected));

        Assert.Equal(AggregateDisposition.Commit, success.Disposition);
        Assert.True(success.Result.IsSuccess);
        Assert.Equal(AggregateDisposition.Discard, failure.Disposition);
        Assert.Same(Rejected, failure.Result.Error);
    }

    /// <summary>The common policy preserves typed values and errors while choosing the disposition.</summary>
    [Fact]
    public void ShouldCommitTypedResultOnlyWhenSuccessful()
    {
        var success = AggregateOutcome.CommitOnSuccess(Result<int>.Success(42));
        var failure = AggregateOutcome.CommitOnSuccess(Result<int>.Failure(Rejected));

        Assert.Equal(AggregateDisposition.Commit, success.Disposition);
        Assert.Equal(42, success.Result.Value);
        Assert.Equal(AggregateDisposition.Discard, failure.Disposition);
        Assert.Same(Rejected, failure.Result.Error);
    }

    /// <summary>The convenience policy rejects default results just like the explicit factories.</summary>
    [Fact]
    public void ShouldRejectUninitializedResultInCommitOnSuccess()
    {
        _ = Assert.Throws<ArgumentException>(() => AggregateOutcome.CommitOnSuccess(default(Result)));
        _ = Assert.Throws<ArgumentException>(() => AggregateOutcome.CommitOnSuccess(default(Result<int>)));
    }

    /// <summary>
    ///     Discarding clears the instance's pending records and invalidates it: its in-memory state no longer
    ///     matches the store, so any later use is refused.
    /// </summary>
    [Fact]
    public async Task ShouldClearAndInvalidateDiscardedAggregateWithPendingRecords()
    {
        var store = new RecordingEventStore();
        var executor = Executor(store);
        var aggregate = new TestAggregate(Uuid.CreateVersion4());
        _ = await executor.ExecuteAsync(aggregate, current =>
        {
            current.ChangeValue(5);
            return AggregateOutcome.Discard(Result.Success);
        }, Context());

        Assert.Empty(aggregate.UncommittedEvents);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(
            aggregate, _ => AggregateOutcome.Commit(Result.Success), Context()).AsTask());

        Assert.Contains("discarded", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, store.Appends);
    }

    /// <summary>A discarded outcome with nothing recorded leaves the instance usable.</summary>
    [Fact]
    public async Task ShouldKeepAggregateUsableAfterDiscardWithNothingPending()
    {
        var store = new RecordingEventStore();
        var id = await SeedAsync(store, 2);
        var executor = Executor(store);
        var aggregate = new TestAggregate(id);
        _ = await executor.ExecuteAsync(aggregate, _ => AggregateOutcome.Discard(Result.Success), Context());

        var result = await executor.ExecuteAsync(aggregate,
            current => AggregateOutcome.Discard(Result<int>.Success(current.Value)), Context());

        Assert.Equal(2, result.Value);
    }

    /// <summary>A canceled token stops execution before the operation runs.</summary>
    [Fact]
    public async Task ShouldHonorCancellation()
    {
        var store = new RecordingEventStore();
        var executor = Executor(store);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var invoked = false;

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
        var store = new RecordingEventStore();
        var executor = Executor(store);

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
        var services = new ServiceCollection();
        _ = services.AddSingleton<IEventStore>(new RecordingEventStore());
        _ = services.AddPortia();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var first = provider.CreateAsyncScope();
        await using var second = provider.CreateAsyncScope();

        var executor = first.ServiceProvider.GetRequiredService<IAggregateExecutor>();

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

        public void Reset()
        {
            Appends = 0;
            Appended.Clear();
        }

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
    }
}
