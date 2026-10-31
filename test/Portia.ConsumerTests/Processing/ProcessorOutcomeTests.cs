using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using static Cntryl.Portia.Consumer.ProcessorOutcomeFixture;

namespace Cntryl.Portia.Consumer;

public sealed partial class ProcessorOutcomeTests
{
    [Theory]
    [InlineData(true, 0, false)]
    [InlineData(true, 1, false)]
    [InlineData(true, 2, false)]
    [InlineData(false, 0, false)]
    [InlineData(false, 1, false)]
    [InlineData(false, 2, false)]
    [InlineData(true, 0, true)]
    [InlineData(true, 1, true)]
    [InlineData(true, 2, true)]
    [InlineData(false, 0, true)]
    [InlineData(false, 1, true)]
    [InlineData(false, 2, true)]
    public async Task FailedResultsStopDispatchAndResumeAtCommittedProgress(bool projection, int mode, bool transient)
    {
        var source = await SeedAsync();
        var state = new State { Error = RequestError.Conflict("private failure", transient) };
        var failure = await Assert.ThrowsAsync<ProcessorFailureException>(() => RunAsync(source, state, projection, mode));

        Assert.Same(state.Error, failure.Error);
        Assert.Equal(transient, failure.IsTransient);
        Assert.Equal(ProcessorFailureStage.Handling, failure.Stage);
        Assert.Equal("outcomes", failure.Identity.ComponentName);
        Assert.Equal(state.Checkpoint, failure.Checkpoint);
        Assert.Equal(mode == 0 ? "1" : "", failure.Checkpoint!.Value.Cursor.ToString());
        Assert.Equal(mode == 0 ? 1UL : 0UL, failure.FirstSource?.ResourceOffset);
        Assert.Equal(mode == 0 ? 1UL : 2UL, failure.LastSource?.ResourceOffset);
        Assert.Equal([1, 2], state.Seen);
        Assert.Equal(projection ? (mode == 0 ? 1 : 0) : 3, state.Value);
        Assert.DoesNotContain("private failure", failure.Message, StringComparison.Ordinal);
        if (projection)
            Assert.Equal(mode == 0 ? 2 : 1, state.Disposals);

        state.Error = null;
        await RunAsync(source, state, projection, mode);
        Assert.Equal("3", state.Checkpoint.Cursor.ToString());
        Assert.Equal(projection ? 6 : (mode == 0 ? 8 : 9), state.Value);
        Assert.Equal<int>(mode == 0 ? [1, 2, 2, 3] : [1, 2, 1, 2, 3], state.Seen);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ThrownExceptionsRetainTheOriginalFailureAndSource(bool projection)
    {
        var original = new IOException("source service failed");
        var state = new State { Exception = original };
        var failure = await Assert.ThrowsAsync<ProcessorFailureException>(async () => await RunAsync(await SeedAsync(), state, projection, 1));

        Assert.Same(original, failure.InnerException);
        Assert.True(failure.IsTransient);
        Assert.Equal(ProcessorFailureStage.Handling, failure.Stage);
        Assert.Equal(ProjectionCheckpoint.Start, state.Checkpoint);
        Assert.Equal([1, 2], state.Seen);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UninitializedResultsCannotAdvanceProgress(bool projection)
    {
        var state = new State { ReturnDefault = true };
        var failure = await Assert.ThrowsAsync<ProcessorFailureException>(async () => await RunAsync(await SeedAsync(), state, projection, 1));

        Assert.IsType<InvalidOperationException>(failure.InnerException);
        Assert.Equal(ProcessorFailureStage.Handling, failure.Stage);
        Assert.Equal(ProjectionCheckpoint.Start, state.Checkpoint);
        Assert.Equal([1, 2], state.Seen);
    }

    [Fact]
    public async Task CleanupFailureCannotReplaceTheHandlerFailure()
    {
        var cleanup = new IOException("cleanup failed");
        var state = new State { Error = RequestError.Validation("bad event"), DisposeException = cleanup };
        var failure = await Assert.ThrowsAsync<ProcessorFailureException>(async () => await RunAsync(await SeedAsync(), state, true, 1));

        Assert.Same(state.Error, failure.Error);
        Assert.Same(cleanup, failure.CleanupException);
        Assert.False(failure.IsTransient);
        Assert.Equal(0, state.Value);
        Assert.Equal(ProjectionCheckpoint.Start, state.Checkpoint);
    }

    [Fact]
    public async Task CleanupAfterACommitReportsTheConfirmedCheckpoint()
    {
        var cleanup = new IOException("cleanup failed");
        var state = new State { DisposeException = cleanup };
        var failure = await Assert.ThrowsAsync<ProcessorFailureException>(async () => await RunAsync(await SeedAsync(), state, true, 1));

        Assert.Equal(ProcessorFailureStage.Disposing, failure.Stage);
        Assert.Same(cleanup, failure.InnerException);
        Assert.Equal(state.Checkpoint, failure.Checkpoint);
        Assert.Equal("3", state.Checkpoint.Cursor.ToString());
        Assert.Equal(6, state.Value);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SourceReadFailuresCarryTheResumeCheckpoint(bool projection)
    {
        var original = new IOException("reader failed");
        var cleanup = new IOException("reader initialization cleanup failed");
        original.Data["Portia.CleanupException"] = cleanup;
        var state = new State { Checkpoint = new ProjectionCheckpoint(new EventCursor("previous")) };
        var failure = await Assert.ThrowsAsync<ProcessorFailureException>(() => RunAsync(new FailedReader(original), state, projection, 1));

        Assert.Same(original, failure.InnerException);
        Assert.Equal(ProcessorFailureStage.Reading, failure.Stage);
        Assert.Same(cleanup, failure.CleanupException);
        Assert.Equal(state.Checkpoint, failure.Checkpoint);
        Assert.Null(failure.FirstSource);
        Assert.Null(failure.LastSource);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task HostedPermanentResultsStopImmediatelyAndTransientResultsRetry(bool projection, bool transient)
    {
        var state = new State { Projection = projection, Error = RequestError.Conflict("unavailable", transient) };
        var clock = new ManualClock();
        var services = new ServiceCollection();
        services.AddSingleton(state);
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<IDomainEventReader>(await SeedAsync());
        var portia = services.AddPortia();
        _ = (projection
            ? portia.AddProjector<GroupedProjection>("outcomes", WorkloadScope.Global)
            : portia.AddReactor<GroupedReaction>("outcomes", WorkloadScope.Global))
            .AddWorkers().UseSingleProcessWorkloads();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var worker = Assert.Single(provider.GetServices<IHostedService>().OfType<BackgroundService>());
        try
        {
            await worker.StartAsync(default);
            if (transient)
            {
                Assert.Equal(TimeSpan.FromSeconds(1), await clock.WaitForDelayAsync());
                Assert.Equal(ProjectionCheckpoint.Start, state.Checkpoint);
                state.Error = null;
                clock.Advance(TimeSpan.FromSeconds(1));
                Assert.Equal(TimeSpan.FromSeconds(1), await clock.WaitForDelayAsync());
                Assert.Equal("3", state.Checkpoint.Cursor.ToString());
                Assert.Equal([1, 2, 1, 2, 3], state.Seen);
                Assert.Equal(2, state.Loads);
            }
            else
            {
                var terminal = await Assert.ThrowsAsync<WorkloadFailureException>(() => worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(3)));
                Assert.Equal(1, terminal.Attempts);
                var failure = Assert.IsType<ProcessorFailureException>(terminal.InnerException);
                Assert.Same(state.Error, failure.Error);
                Assert.Equal(ProjectionCheckpoint.Start, state.Checkpoint);
                Assert.Equal(1, state.Loads);
            }
        }
        finally
        {
            if (transient)
                await worker.StopAsync(default);
            worker.Dispose();
        }
    }

    [Theory]
    [InlineData(true, true, 0)]
    [InlineData(false, true, 0)]
    [InlineData(true, false, 0)]
    [InlineData(false, false, 0)]
    [InlineData(true, true, 1)]
    [InlineData(false, true, 1)]
    [InlineData(true, false, 1)]
    [InlineData(false, false, 1)]
    [InlineData(true, true, 2)]
    [InlineData(false, true, 2)]
    [InlineData(true, false, 2)]
    [InlineData(false, false, 2)]
    [InlineData(true, true, 3)]
    [InlineData(false, true, 3)]
    [InlineData(true, false, 3)]
    [InlineData(false, false, 3)]
    public async Task HostedExceptionsRespectClassificationAndRetainTheCause(bool projection, bool transient, int kind)
    {
        Exception original = kind switch
        {
            1 => new ProviderException(transient),
            2 => new RequestGuardException(RequestError.Conflict("guard rejected", transient)),
            3 => new RequestAuthorizationException(RequestError.Forbidden("authorization rejected", transient)),
            _ => transient ? new IOException("dependency unavailable") : new NotSupportedException("unsupported operation")
        };
        var state = new State { Projection = projection, Exception = original };
        var clock = new ManualClock();
        var services = new ServiceCollection();
        services.AddSingleton(state);
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<IDomainEventReader>(await SeedAsync());
        var portia = services.AddPortia();
        _ = (projection
            ? portia.AddProjector<GroupedProjection>("outcomes", WorkloadScope.Global, options => options.FailureAttemptLimit = 2)
            : portia.AddReactor<GroupedReaction>("outcomes", WorkloadScope.Global, options => options.FailureAttemptLimit = 2))
            .AddWorkers().UseSingleProcessWorkloads();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var worker = Assert.Single(provider.GetServices<IHostedService>().OfType<BackgroundService>());
        try
        {
            await worker.StartAsync(default);
            if (transient)
            {
                Assert.Equal(TimeSpan.FromSeconds(1), await clock.WaitForDelayAsync());
                clock.Advance(TimeSpan.FromSeconds(1));
            }
            var terminal = await Assert.ThrowsAsync<WorkloadFailureException>(() => worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal(transient ? 2 : 1, terminal.Attempts);
            var failure = Assert.IsType<ProcessorFailureException>(terminal.InnerException);
            Assert.Same(original, failure.InnerException);
            Assert.Equal(ProjectionCheckpoint.Start, state.Checkpoint);
            Assert.Equal(transient ? 2 : 1, state.Loads);
        }
        finally
        {
            worker.Dispose();
        }
    }

    [Fact]
    public async Task TypedContextsShareMetadataOnlyWithinTheirTransaction()
    {
        var state = new State();
        _ = await new ProjectorRunner(await SeedAsync()).RunAsync(new GroupedProjection(state),
            ProjectionCheckpoint.Start, new ProjectionRunOptions { MaxBatchSize = 2, RebuildId = "candidate" });

        Assert.Equal(3, state.Contexts.Count);
        Assert.Same(state.Contexts[0].Batch, state.Contexts[1].Batch);
        Assert.NotSame(state.Contexts[1].Batch, state.Contexts[2].Batch);
        Assert.NotSame(state.Contexts[0], state.Contexts[1]);
        Assert.All(state.Contexts, context =>
        {
            Assert.Same(context.Trigger, context.Source.Event);
            Assert.Same(context.Batch.Identity, context.Identity);
            Assert.True(context.IsRebuild);
            Assert.Equal("candidate", context.Identity.RebuildId);
        });
        var first = state.Contexts[0];
        _ = Assert.Throws<ArgumentException>(() => new ProjectorContext<Changed>(new Changed(1), first.Source, first.Batch));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SourceCleanupCannotReplaceADeclaredFailure(bool projection)
    {
        var cleanup = new IOException("source cleanup failed");
        var transactionCleanup = new IOException("transaction cleanup failed");
        var state = new State
        {
            Error = RequestError.Validation("bad event"),
            DisposeException = projection ? transactionCleanup : null
        };
        var source = new CleanupReader(await SeedAsync(), cleanup);
        var failure = await Assert.ThrowsAsync<ProcessorFailureException>(() => RunAsync(source, state, projection, 1));

        Assert.Same(state.Error, failure.Error);
        Assert.False(failure.IsTransient);
        Assert.Equal(ProcessorFailureStage.Handling, failure.Stage);
        Assert.Equal(ProjectionCheckpoint.Start, state.Checkpoint);
        if (projection)
            Assert.Equal([transactionCleanup, cleanup], Assert.IsType<AggregateException>(failure.CleanupException).InnerExceptions);
        else
            Assert.Same(cleanup, failure.CleanupException);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SourceCleanupAfterSuccessReportsConfirmedProgress(bool projection)
    {
        var cleanup = new IOException("source cleanup failed");
        var state = new State();
        var source = new CleanupReader(await SeedAsync(), cleanup);
        var failure = await Assert.ThrowsAsync<ProcessorFailureException>(() => RunAsync(source, state, projection, 1));

        Assert.Equal(ProcessorFailureStage.Disposing, failure.Stage);
        Assert.Same(cleanup, failure.InnerException);
        Assert.Equal(state.Checkpoint, failure.Checkpoint);
        Assert.Equal("3", state.Checkpoint.Cursor.ToString());
    }

    static async Task RunAsync(IDomainEventReader source, State state, bool projection, int mode)
    {
        state.Projection = projection;
        if (projection)
        {
            Projector component = mode switch { 0 => new SingleProjection(state), 1 => new GroupedProjection(state), _ => new BulkProjection(state) };
            _ = await new ProjectorRunner(source).RunAsync(component, state.Checkpoint);
        }
        else
        {
            Reactor component = mode switch { 0 => new SingleReaction(state), 1 => new GroupedReaction(state), _ => new BulkReaction(state) };
            _ = await new ReactorRunner(source).RunAsync(component, state.Checkpoint);
        }
    }

    static async Task<InMemoryEventStore> SeedAsync()
    {
        var store = new InMemoryEventStore();
        var id = Uuid.CreateVersion4();
        await store.AppendAsync(new EventStreamAddress("outcome-source", "events", id.ToString()), 0,
            [DomainEventSeed.Attach(new Changed(1), id, 1), DomainEventSeed.Attach(new Changed(2), id, 2), DomainEventSeed.Attach(new Changed(3), id, 3)]);
        return store;
    }

    sealed class ProviderException(bool transient) : System.Data.Common.DbException
    {
        public override bool IsTransient => transient;
    }

    sealed class CleanupReader(IDomainEventReader source, Exception cleanup) : IDomainEventReader
    {
        public IAsyncEnumerable<DomainEventRecord> ReadAsync(EventStreamAddress stream, ulong fromOffset, CancellationToken ct) => throw new NotSupportedException();
        public IAsyncEnumerable<DomainEventRecord> ReadAsync(EventStreamPattern pattern, EventCursor cursor, CancellationToken ct) =>
            new Records(source.ReadAsync(pattern, cursor, ct), cleanup);
        sealed class Records(IAsyncEnumerable<DomainEventRecord> source, Exception cleanup) : IAsyncEnumerable<DomainEventRecord>
        {
            public IAsyncEnumerator<DomainEventRecord> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
                new Cursor(source.GetAsyncEnumerator(cancellationToken), cleanup);
        }
        sealed class Cursor(IAsyncEnumerator<DomainEventRecord> source, Exception cleanup) : IAsyncEnumerator<DomainEventRecord>
        {
            public DomainEventRecord Current => source.Current;
            public ValueTask<bool> MoveNextAsync() => source.MoveNextAsync();
            public async ValueTask DisposeAsync()
            {
                await source.DisposeAsync();
                throw cleanup;
            }
        }
    }

    sealed class FailedReader(Exception error) : IDomainEventReader
    {
        public IAsyncEnumerable<DomainEventRecord> ReadAsync(EventStreamAddress stream, ulong fromOffset, CancellationToken ct) => throw new NotSupportedException();
        public async IAsyncEnumerable<DomainEventRecord> ReadAsync(EventStreamPattern pattern, EventCursor cursor, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.FromException(error);
            yield break;
        }
    }
}
