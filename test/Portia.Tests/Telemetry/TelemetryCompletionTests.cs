using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

/// <summary>Verifies every timed operation completes with the outcome that actually ended it.</summary>
[Collection(TelemetryTestGroup.Name)]
public sealed class TelemetryCompletionTests
{
    /// <summary>Authorization records both requested and unexpected cancellation exactly once.</summary>
    /// <param name="requested">Whether the authorizer requests caller cancellation before throwing.</param>
    /// <param name="expectedOutcome">The bounded telemetry outcome.</param>
    [Theory]
    [InlineData(false, "fault")]
    [InlineData(true, "canceled")]
    public async Task ShouldCompleteAuthorizationGivenCancellation(bool requested, string expectedOutcome)
    {
        using var measurements = new OutcomeMeasurements();
        using var cancellation = new CancellationTokenSource();
        var failure = new AuthorizationFailure(cancellation, requested);
        var handler = new RequestRegistration<AuthorizationProbe, AuthorizationProbeHandler>();
        var authorizer = new RequestAuthorizerRegistration<AuthorizationProbe, CancelingAuthorizer>();
        var services = new ServiceCollection()
            .AddSingleton(failure)
            .AddSingleton<AuthorizationProbeHandler>()
            .AddSingleton<CancelingAuthorizer>()
            .BuildServiceProvider();
        var bus = new RequestBus(services, new RequestRegistry([handler], [authorizer], [], []));

        _ = await Assert.ThrowsAsync<OperationCanceledException>(() => bus.DispatchAsync(
            new AuthorizationProbe(), bus.CreateContext(RequestActor.System), cancellation.Token).AsTask());

        Assert.Equal(expectedOutcome, measurements.SingleOutcome("portia.authorization.duration"));
    }

    /// <summary>Every guard that runs records its duration with its component name and outcome.</summary>
    /// <param name="fails">Whether the guard fails with a conflict.</param>
    /// <param name="expectedOutcome">The bounded telemetry outcome.</param>
    [Theory]
    [InlineData(false, "success")]
    [InlineData(true, "conflict")]
    public async Task ShouldRecordGuardDurationWithComponentNameAndOutcome(bool fails, string expectedOutcome)
    {
        using var measurements = new OutcomeMeasurements();
        var bus = GuardBus(new GuardBehavior(fails ? GuardAction.Conflict : GuardAction.Pass),
            new RequestGuardRegistration<GuardTelemetryProbe, TelemetryOutcomeGuard>());

        _ = await bus.DispatchAsync(new GuardTelemetryProbe(), bus.CreateContext(RequestActor.System));

        Assert.Equal(expectedOutcome, measurements.SingleOutcome("portia.guard.duration",
            "portia.component.name", nameof(TelemetryOutcomeGuard)));
    }

    /// <summary>A guard records both requested and unexpected cancellation exactly once.</summary>
    /// <param name="requested">Whether the guard requests caller cancellation before throwing.</param>
    /// <param name="expectedOutcome">The bounded telemetry outcome.</param>
    [Theory]
    [InlineData(false, "fault")]
    [InlineData(true, "canceled")]
    public async Task ShouldCompleteGuardTelemetryGivenCancellation(bool requested, string expectedOutcome)
    {
        using var measurements = new OutcomeMeasurements();
        using var cancellation = new CancellationTokenSource();
        var bus = GuardBus(new GuardBehavior(GuardAction.Cancel, cancellation, requested),
            new RequestGuardRegistration<GuardTelemetryProbe, TelemetryCancelingGuard>());

        _ = await Assert.ThrowsAsync<OperationCanceledException>(() => bus.DispatchAsync(
            new GuardTelemetryProbe(), bus.CreateContext(RequestActor.System), cancellation.Token).AsTask());

        Assert.Equal(expectedOutcome, measurements.SingleOutcome("portia.guard.duration",
            "portia.component.name", nameof(TelemetryCancelingGuard)));
    }

    /// <summary>A guard returning an uninitialized result is recorded as a fault.</summary>
    [Fact]
    public async Task ShouldRecordFaultGuardTelemetryGivenUninitializedResult()
    {
        using var measurements = new OutcomeMeasurements();
        var bus = GuardBus(new GuardBehavior(GuardAction.Uninitialized),
            new RequestGuardRegistration<GuardTelemetryProbe, TelemetryUninitializedGuard>());

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => bus.DispatchAsync(
            new GuardTelemetryProbe(), bus.CreateContext(RequestActor.System)).AsTask());

        Assert.Equal("fault", measurements.SingleOutcome("portia.guard.duration",
            "portia.component.name", nameof(TelemetryUninitializedGuard)));
    }

    /// <summary>Guards after the first failure never run, so they record nothing.</summary>
    [Fact]
    public async Task ShouldNotRecordGuardTelemetryForGuardsAfterFirstFailure()
    {
        using var measurements = new OutcomeMeasurements();
        var bus = GuardBus(new GuardBehavior(GuardAction.Conflict),
            new RequestGuardRegistration<GuardTelemetryProbe, TelemetryOutcomeGuard>(),
            new RequestGuardRegistration<GuardTelemetryProbe, TelemetrySkippedGuard>());

        _ = await bus.DispatchAsync(new GuardTelemetryProbe(), bus.CreateContext(RequestActor.System));

        Assert.Equal("conflict", measurements.SingleOutcome("portia.guard.duration",
            "portia.component.name", nameof(TelemetryOutcomeGuard)));
        Assert.Equal(0, measurements.Count("portia.guard.duration", "portia.component.name",
            nameof(TelemetrySkippedGuard)));
    }

    /// <summary>A stream ended by a failed guard reports the guard's error as the request outcome.</summary>
    [Fact]
    public async Task ShouldRecordStreamGuardFailureAsRequestOutcome()
    {
        using var measurements = new OutcomeMeasurements();
        var services = new ServiceCollection()
            .AddSingleton(new GuardBehavior(GuardAction.Conflict))
            .AddSingleton<GuardTelemetryStreamHandler>()
            .AddSingleton<TelemetryStreamGuard>()
            .BuildServiceProvider();
        var bus = new RequestBus(services, new RequestRegistry(
            [new StreamRequestRegistration<GuardTelemetryStream, GuardTelemetryStreamHandler, int>()], [], [],
            [new RequestGuardRegistration<GuardTelemetryStream, TelemetryStreamGuard>()], []));

        _ = await Assert.ThrowsAsync<RequestGuardException>(async () =>
        {
            await foreach (var _ in bus.DispatchStreamAsync(new GuardTelemetryStream(),
                               bus.CreateContext(RequestActor.System)))
            {
            }
        });

        Assert.Equal("conflict", measurements.SingleOutcome("portia.request.duration",
            "portia.request.name", nameof(GuardTelemetryStream)));
        Assert.Equal("conflict", measurements.SingleOutcome("portia.guard.duration",
            "portia.component.name", nameof(TelemetryStreamGuard)));
    }

    /// <summary>A commit-time concurrency exception is a settled conflict while still propagating.</summary>
    [Fact]
    public async Task ShouldRecordCommitConcurrencyAsRequestConflict()
    {
        using var measurements = new OutcomeMeasurements();
        var handler = new ConcurrencyTelemetryHandler();
        var services = new ServiceCollection()
            .AddSingleton(handler)
            .BuildServiceProvider();
        var bus = new RequestBus(services, new RequestRegistry(
            [new RequestRegistration<ConcurrencyTelemetryProbe, ConcurrencyTelemetryHandler>()], [], [], []));

        var error = await Assert.ThrowsAsync<EventStreamConcurrencyException>(() => bus.DispatchAsync(
            new ConcurrencyTelemetryProbe(), bus.CreateContext(RequestActor.System)).AsTask());

        Assert.Contains("sensitive", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, handler.Attempts);
        Assert.Equal("conflict", measurements.SingleOutcome("portia.request.duration",
            "portia.request.name", nameof(ConcurrencyTelemetryProbe)));
    }

    /// <summary>A stream commit race remains a non-disclosing conflict during move or disposal.</summary>
    /// <param name="onDispose">Whether the stream reports the conflict while being disposed.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldRecordStreamCommitConcurrencyAsRequestConflict(bool onDispose)
    {
        using var measurements = new OutcomeMeasurements();
        using var listener = ListenToActivities(out var stopped);
        var services = new ServiceCollection()
            .AddSingleton(new StreamConcurrencyBehavior(onDispose))
            .AddSingleton<ConcurrencyTelemetryStreamHandler>()
            .BuildServiceProvider();
        var bus = new RequestBus(services, new RequestRegistry(
            [new StreamRequestRegistration<ConcurrencyTelemetryStream, ConcurrencyTelemetryStreamHandler, int>()],
            [], [], [], []));

        var observed = 0;
        var error = await Assert.ThrowsAsync<EventStreamConcurrencyException>(async () =>
        {
            await foreach (var _ in bus.DispatchStreamAsync(new ConcurrencyTelemetryStream(),
                               bus.CreateContext(RequestActor.System)))
            {
                observed++;
                if (onDispose)
                    break;
            }
        });

        Assert.Contains("sensitive", error.Message, StringComparison.Ordinal);
        Assert.Equal(onDispose ? 1 : 0, observed);
        Assert.Equal("conflict", measurements.SingleOutcome("portia.request.duration",
            "portia.request.name", nameof(ConcurrencyTelemetryStream)));
        var execute = Assert.Single(stopped, activity =>
            activity.OperationName == PortiaTelemetry.ExecuteActivityName &&
            Equals(activity.GetTagItem("portia.request.name"), nameof(ConcurrencyTelemetryStream)));
        Assert.Equal("conflict", execute.GetTagItem("portia.outcome"));
        Assert.DoesNotContain(execute.Events, activityEvent => activityEvent.Name == "exception");
    }

    static RequestBus GuardBus(GuardBehavior behavior, params RequestGuardRegistration[] guards)
    {
        var services = new ServiceCollection()
            .AddSingleton(behavior)
            .AddSingleton<GuardTelemetryProbeHandler>()
            .AddSingleton<TelemetryOutcomeGuard>()
            .AddSingleton<TelemetryCancelingGuard>()
            .AddSingleton<TelemetryUninitializedGuard>()
            .AddSingleton<TelemetrySkippedGuard>()
            .BuildServiceProvider();
        return new RequestBus(services, new RequestRegistry(
            [new RequestRegistration<GuardTelemetryProbe, GuardTelemetryProbeHandler>()], [], [], guards, []));
    }

    /// <summary>An event reader's unrelated cancellation exception is an aggregate fault.</summary>
    [Fact]
    public async Task ShouldReportFaultGivenUnrequestedAggregateCancellation()
    {
        using var measurements = new OutcomeMeasurements();
        var repository = new AggregateRepository(new CancelingEventStore());

        _ = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            repository.HydrateAsync(new TestAggregate(Uuid.CreateVersion4())).AsTask());

        Assert.Equal("fault", measurements.SingleOutcome("portia.aggregate.operation.duration"));
    }

    /// <summary>A projector's unrelated cancellation exception is a processor fault.</summary>
    [Fact]
    public async Task ShouldReportFaultGivenUnrequestedProjectorCancellation()
    {
        using var measurements = new OutcomeMeasurements();
        var source = await SourceWithOneEventAsync();
        var runner = new ProjectorRunner(source);

        _ = await Assert.ThrowsAsync<OperationCanceledException>(() => runner.RunAsync(
            new CancelingProjector(), ProjectionCheckpoint.Start).AsTask());

        Assert.Equal("fault", measurements.SingleOutcome("portia.processor.batch.duration", "projector"));
    }

    /// <summary>A reactor's unrelated cancellation exception is a processor fault.</summary>
    [Fact]
    public async Task ShouldReportFaultGivenUnrequestedReactorCancellation()
    {
        using var measurements = new OutcomeMeasurements();
        var source = await SourceWithOneEventAsync();
        var runner = new ReactorRunner(source);

        _ = await Assert.ThrowsAsync<OperationCanceledException>(() => runner.RunAsync(
            new CancelingReactor(), ProjectionCheckpoint.Start).AsTask());

        Assert.Equal("fault", measurements.SingleOutcome("portia.processor.batch.duration", "reactor"));
    }

    /// <summary>Every Fitz append failure completes one duration with its actual failure outcome.</summary>
    /// <param name="failureStage">The append lifecycle stage that fails.</param>
    /// <param name="cancelBeforeFailure">Whether the caller token is canceled immediately before an I/O failure.</param>
    [Theory]
    [InlineData("begin", false)]
    [InlineData("append", true)]
    [InlineData("dispose", false)]
    public async Task ShouldCompleteFitzAppendTelemetryGivenFailure(string failureStage, bool cancelBeforeFailure)
    {
        using var measurements = new OutcomeMeasurements();
        using var cancellation = new CancellationTokenSource();
        var streams = new FailingAppendStreams(failureStage, cancellation, cancelBeforeFailure);
        var serializer = TestJson.DomainSerializer(
            new DomainEventTypeCatalog().Register<ValueChanged>(1, "test.value.changed"));
        var store = new FitzEventStore(streams, serializer);
        var id = Uuid.CreateVersion4();
        var ev = new ValueChanged(1);
        ev.AttachMetadata(new DomainEventMetadata(Uuid.CreateVersion4(), id, 1, DateTimeOffset.UtcNow));

        _ = await Assert.ThrowsAsync<IOException>(() => store.AppendAsync(
            new EventStreamAddress("test", "telemetry", id.ToString()), 0, [ev], cancellation.Token).AsTask());

        Assert.Equal("fault", measurements.SingleOutcome("portia.event_store.operation.duration"));
    }

    static async ValueTask<InMemoryEventStore> SourceWithOneEventAsync()
    {
        var source = new InMemoryEventStore();
        var id = Uuid.CreateVersion4();
        var ev = new ValueChanged(1);
        ev.AttachMetadata(new DomainEventMetadata(Uuid.CreateVersion4(), id, 1, DateTimeOffset.UtcNow));
        await source.AppendAsync(new EventStreamAddress("test", "telemetry", id.ToString()), 0, [ev]);
        return source;
    }

    internal sealed record AuthorizationProbe : IRequest;

    internal sealed class AuthorizationProbeHandler : IRequestHandler<AuthorizationProbe>
    {
        public ValueTask<Result> HandleAsync(IRequestContext<AuthorizationProbe> context, CancellationToken ct) =>
            ValueTask.FromResult(Result.Success);
    }

    internal sealed record AuthorizationFailure(CancellationTokenSource Source, bool Requested);

    internal sealed record GuardTelemetryProbe : IRequest;

    internal sealed record ConcurrencyTelemetryProbe : IRequest;

    internal sealed class ConcurrencyTelemetryHandler : IRequestHandler<ConcurrencyTelemetryProbe>
    {
        public int Attempts { get; private set; }

        public ValueTask<Result> HandleAsync(IRequestContext<ConcurrencyTelemetryProbe> context,
            CancellationToken ct)
        {
            Attempts++;
            throw new EventStreamConcurrencyException("sensitive stream address");
        }
    }

    internal sealed record ConcurrencyTelemetryStream : IStreamRequest<int>;

    internal sealed record StreamConcurrencyBehavior(bool OnDispose);

    internal sealed class ConcurrencyTelemetryStreamHandler(StreamConcurrencyBehavior behavior)
        : IStreamRequestHandler<ConcurrencyTelemetryStream, int>
    {
        public IAsyncEnumerable<int> HandleAsync(IRequestContext<ConcurrencyTelemetryStream> context,
            CancellationToken ct) => new ConcurrencyTelemetryStreamEnumerable(behavior.OnDispose);
    }

    sealed class ConcurrencyTelemetryStreamEnumerable(bool onDispose)
        : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        bool _moved;

        public int Current => 1;

        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken ct = default) => this;

        public ValueTask<bool> MoveNextAsync()
        {
            if (!onDispose)
                return ValueTask.FromException<bool>(Conflict());
            if (_moved)
                return ValueTask.FromResult(false);
            _moved = true;
            return ValueTask.FromResult(true);
        }

        public ValueTask DisposeAsync() => onDispose
            ? ValueTask.FromException(Conflict())
            : ValueTask.CompletedTask;

        static EventStreamConcurrencyException Conflict() =>
            new("sensitive Fitz stream address");
    }

    internal sealed class GuardTelemetryProbeHandler : IRequestHandler<GuardTelemetryProbe>
    {
        public ValueTask<Result> HandleAsync(IRequestContext<GuardTelemetryProbe> context, CancellationToken ct) =>
            ValueTask.FromResult(Result.Success);
    }

    internal enum GuardAction
    {
        Pass,
        Conflict,
        Cancel,
        Uninitialized
    }

    internal sealed record GuardBehavior(
        GuardAction Action,
        CancellationTokenSource? Source = null,
        bool Requested = false)
    {
        public ValueTask<Result> RunAsync(CancellationToken ct)
        {
            switch (Action)
            {
                case GuardAction.Conflict:
                    return ValueTask.FromResult(Result.Failure(new RequestError(RequestErrorKind.Conflict, "taken")));
                case GuardAction.Cancel:
                    if (Requested)
                        Source!.Cancel();
                    return ValueTask.FromException<Result>(new OperationCanceledException(ct));
                case GuardAction.Uninitialized:
                    return ValueTask.FromResult(default(Result));
                default:
                    return ValueTask.FromResult(Result.Success);
            }
        }
    }

    internal sealed class TelemetryOutcomeGuard(GuardBehavior behavior) : IRequestGuard<GuardTelemetryProbe>
    {
        public ValueTask<Result> GuardAsync(IRequestContext<GuardTelemetryProbe> context, CancellationToken ct) =>
            behavior.RunAsync(ct);
    }

    internal sealed class TelemetryCancelingGuard(GuardBehavior behavior) : IRequestGuard<GuardTelemetryProbe>
    {
        public ValueTask<Result> GuardAsync(IRequestContext<GuardTelemetryProbe> context, CancellationToken ct) =>
            behavior.RunAsync(ct);
    }

    internal sealed class TelemetryUninitializedGuard(GuardBehavior behavior) : IRequestGuard<GuardTelemetryProbe>
    {
        public ValueTask<Result> GuardAsync(IRequestContext<GuardTelemetryProbe> context, CancellationToken ct) =>
            behavior.RunAsync(ct);
    }

    internal sealed record GuardTelemetryStream : IStreamRequest<int>;

    internal sealed class GuardTelemetryStreamHandler : IStreamRequestHandler<GuardTelemetryStream, int>
    {
        public async IAsyncEnumerable<int> HandleAsync(IRequestContext<GuardTelemetryStream> context,
            [EnumeratorCancellation] CancellationToken ct)
        {
            yield return 1;
            await Task.CompletedTask;
        }
    }

    internal sealed class TelemetryStreamGuard(GuardBehavior behavior) : IRequestGuard<GuardTelemetryStream>
    {
        public ValueTask<Result> GuardAsync(IRequestContext<GuardTelemetryStream> context, CancellationToken ct) =>
            behavior.RunAsync(ct);
    }

    internal sealed class TelemetrySkippedGuard : IRequestGuard<GuardTelemetryProbe>
    {
        public ValueTask<Result> GuardAsync(IRequestContext<GuardTelemetryProbe> context, CancellationToken ct) =>
            ValueTask.FromResult(Result.Success);
    }

    internal sealed class CancelingAuthorizer(AuthorizationFailure failure) : IRequestAuthorizer<AuthorizationProbe>
    {
        public ValueTask<Result> AuthorizeAsync(IRequestContext<AuthorizationProbe> context, CancellationToken ct)
        {
            if (failure.Requested)
            {
                failure.Source.Cancel();
            }

            return ValueTask.FromException<Result>(new OperationCanceledException(ct));
        }
    }

    sealed class CancelingEventStore : IEventStore
    {
        public IAsyncEnumerable<DomainEventRecord> ReadAsync(EventStreamAddress stream, ulong fromOffset = 0,
            CancellationToken ct = default) => new CancelingRecords();

        public IAsyncEnumerable<DomainEventRecord> ReadAsync(EventStreamPattern pattern, EventCursor cursor,
            CancellationToken ct = default) => new CancelingRecords();

        public ValueTask AppendAsync(EventStreamAddress stream, ulong expectedStreamPosition,
            IReadOnlyList<DomainEvent> events, CancellationToken ct = default) =>
            ValueTask.FromException(new OperationCanceledException());
    }

    sealed class CancelingRecords : IAsyncEnumerable<DomainEventRecord>, IAsyncEnumerator<DomainEventRecord>
    {
        public IAsyncEnumerator<DomainEventRecord> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
            this;

        public DomainEventRecord Current => throw new InvalidOperationException();

        public ValueTask<bool> MoveNextAsync() => ValueTask.FromException<bool>(new OperationCanceledException());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    sealed class CancelingProjector() : Projector(new RecordingProjectionTarget(),
        EventStreamPattern.ForPattern("test", "telemetry"))
    {
        protected override ValueTask ProjectEventAsync(DomainEventRecord record, IProjectorContext context,
            CancellationToken ct) => ValueTask.FromException(new OperationCanceledException());
    }

    sealed class CancelingReactor() : Reactor(new InMemoryProjectionCheckpointStore(),
        EventStreamPattern.ForPattern("test", "telemetry"))
    {
        protected override ValueTask ReactToEventAsync(DomainEventRecord record, IExecutionContext context,
            CancellationToken ct) => ValueTask.FromException(new OperationCanceledException());
    }

    sealed class FailingAppendStreams(
        string failureStage,
        CancellationTokenSource cancellation,
        bool cancelBeforeFailure) : IStreamClient
    {
        public Task<IStreamSession> BeginAsync(string route, ReadOnlyMemory<byte>? ingestMetadata = null,
            CancellationToken ct = default) => failureStage == "begin"
            ? Task.FromException<IStreamSession>(new IOException("begin failed"))
            : Task.FromResult<IStreamSession>(new FailingAppendSession(failureStage, cancellation,
                cancelBeforeFailure));

        public IAsyncEnumerable<StreamRecord> ReadAsync(string route, ulong startOffset, ulong limit = 100,
            StreamFilterSet? filter = null, ulong? maxBytes = null, ulong? cursorFingerprint = null,
            ulong? capturedWatermark = null, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<StreamReadPage> ReadPageAsync(string route, ulong startOffset, ulong limit = 100,
            StreamFilterSet? filter = null, ulong? maxBytes = null, ulong? cursorFingerprint = null,
            ulong? capturedWatermark = null, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<StreamRecord?> PeekAsync(string route, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StreamMetadata> MetadataAsync(string route, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StreamSubscription> SubscribeAsync(string pattern, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    sealed class FailingAppendSession(
        string failureStage,
        CancellationTokenSource cancellation,
        bool cancelBeforeFailure) : IStreamSession
    {
        public Task<ulong?> AppendAsync(ulong expectedOffset, ReadOnlyMemory<byte> body,
            ReadOnlyMemory<byte>? metadata = null, string? discriminator = null, CancellationToken ct = default)
        {
            if (failureStage != "append")
            {
                return Task.FromResult<ulong?>(expectedOffset);
            }

            if (cancelBeforeFailure)
            {
                cancellation.Cancel();
            }

            return Task.FromException<ulong?>(new IOException("append failed"));
        }

        public Task CommitAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task RollbackAsync(CancellationToken ct = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => failureStage == "dispose"
            ? ValueTask.FromException(new IOException("dispose failed"))
            : ValueTask.CompletedTask;
    }

    sealed class OutcomeMeasurements : IDisposable
    {
        readonly MeterListener _listener;
        readonly List<(string Name, KeyValuePair<string, object?>[] Tags)> _measurements = [];

        public OutcomeMeasurements()
        {
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == PortiaTelemetry.SourceName)
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                }
            };
            _listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
                _measurements.Add((instrument.Name, tags.ToArray())));
            _listener.Start();
        }

        public void Dispose() => _listener.Dispose();

        public string SingleOutcome(string name, string tagKey, string tagValue)
        {
            var measurement = Assert.Single(_measurements, item => item.Name == name &&
                                                                   item.Tags.Any(tag => tag.Key == tagKey &&
                                                                       Equals(tag.Value, tagValue)));
            return Assert.IsType<string>(Assert.Single(measurement.Tags,
                tag => tag.Key == "portia.outcome").Value);
        }

        public int Count(string name, string tagKey, string tagValue) => _measurements.Count(item =>
            item.Name == name && item.Tags.Any(tag => tag.Key == tagKey && Equals(tag.Value, tagValue)));

        public string SingleOutcome(string name, string? runner = null)
        {
            var measurement = Assert.Single(_measurements, item => item.Name == name &&
                                                                   (runner is null || item.Tags.Any(tag =>
                                                                       tag.Key == "portia.runner.name" &&
                                                                       Equals(tag.Value, runner))));
            return Assert.IsType<string>(Assert.Single(measurement.Tags,
                tag => tag.Key == "portia.outcome").Value);
        }
    }

    static ActivityListener ListenToActivities(out ConcurrentQueue<Activity> stopped)
    {
        var activities = new ConcurrentQueue<Activity>();
        stopped = activities;
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == PortiaTelemetry.SourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activities.Enqueue
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}
