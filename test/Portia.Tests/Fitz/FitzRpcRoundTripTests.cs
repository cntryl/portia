using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using Cntryl.Fitz;

namespace Cntryl.Portia;

/// <summary>
///     Proves the RPC path actually round-trips end to end: <see cref="FitzRemoteRequestSender" />
///     sending a request all the way through <see cref="FitzRpcRequestServer" />, the same
///     <see cref="IRequestBus" /> dispatch every other transport uses, and back — not just that each
///     side compiles against the shared contract in isolation. <see cref="InMemoryRpcClient" /> stands in
///     for the real Fitz broker, wiring a registered worker directly to a caller's <c>CallAsync</c>.
/// </summary>
public sealed class FitzRpcRoundTripTests
{
    const string RpcGetValueRoute = "rpc://test/rpc/value/get";
    const string RpcChangeValueRoute = "rpc://test/rpc/value/change";
    static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(5);

    /// <summary>
    ///     Verifies that a result-bearing request sent over RPC reaches its handler and the result
    ///     comes back correctly deserialized.
    /// </summary>
    [Fact]
    public async Task ShouldRoundTripResultBearingRequestOverRpc()
    {
        var rpc = new InMemoryRpcClient();
        var serializer = TestJson.Serializer(typeof(RpcGetValue));
        using var busHost = TestRequestBus.Create();
        var server = new FitzRpcRequestServer(rpc, busHost.ScopeFactory);
        _ = await server.RegisterAsync<RpcGetValue, int>();
        var sender = new FitzRemoteRequestSender(rpc, serializer, serializer);

        var result = await sender.SendAsync<RpcGetValue, int>(new RpcGetValue(), new RequestRouteValues(), null);

        Assert.True(result.IsSuccess);
        Assert.Equal(7, result.Value);
    }

    /// <summary>A final Fitz response is carried as a completed result in the outcome-aware API.</summary>
    [Fact]
    public async Task ShouldReportCompletedOutcomeWhenFinalRpcResponseArrives()
    {
        var rpc = new InMemoryRpcClient();
        var serializer = TestJson.Serializer(typeof(RpcGetValue));
        using var busHost = TestRequestBus.Create();
        var server = new FitzRpcRequestServer(rpc, busHost.ScopeFactory);
        _ = await server.RegisterAsync<RpcGetValue, int>();
        var sender = new FitzRemoteRequestSender(rpc, serializer, serializer);

        var outcome = await sender.SendWithOutcomeAsync<RpcGetValue, int>(
            new RpcGetValue(), new RequestRouteValues(), null);

        var completed = Assert.IsType<RemoteRequestOutcome<int>.Completed>(outcome);
        Assert.True(completed.Result.IsSuccess);
        Assert.Equal(7, completed.Result.Value);
    }

    /// <summary>Pre-cancelled sends stay undispatched and remain visible in send telemetry.</summary>
    [Fact]
    public async Task ShouldRecordTelemetryForPreCancelledOutcomeSends()
    {
        var rpc = new InMemoryRpcClient();
        var serializer = TestJson.Serializer(typeof(RpcGetValue));
        var changeSerializer = TestJson.Serializer(typeof(RpcChangeValue));
        var dispatched = 0;
        var changeDispatched = 0;
        await using var worker = await rpc.RegisterWorkerAsync(RpcGetValueRoute, (_, _, _) =>
        {
            Interlocked.Increment(ref dispatched);
            return ValueTask.CompletedTask;
        });
        await using var changeWorker = await rpc.RegisterWorkerAsync(RpcChangeValueRoute, (_, _, _) =>
        {
            Interlocked.Increment(ref changeDispatched);
            return ValueTask.CompletedTask;
        });
        using var telemetry = new SendTelemetryCapture();
        var sender = new FitzRemoteRequestSender(rpc, serializer, serializer);
        var noResultSender = new FitzRemoteRequestSender(rpc, changeSerializer, changeSerializer);
        using var parent = new Activity("test.pre-cancelled-rpc-send").Start();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var outcome = await sender.SendWithOutcomeAsync<RpcGetValue, int>(
            new RpcGetValue(), new RequestRouteValues(), null, cancellation.Token);
        var noResultOutcome = await noResultSender.SendWithOutcomeAsync(
            new RpcChangeValue(1), new RequestRouteValues(), null, cancellation.Token);

        _ = Assert.IsType<RemoteRequestOutcome<int>.NotDispatched>(outcome);
        _ = Assert.IsType<RemoteRequestOutcome.NotDispatched>(noResultOutcome);
        Assert.Equal(0, dispatched);
        Assert.Equal(0, changeDispatched);
        telemetry.AssertPreCancelledSends(parent.SpanId.ToString(), "test.rpc.get-value", "test.rpc.change-value");
    }

    /// <summary>A forwarded cancellation is reported without claiming that the remote handler observed it.</summary>
    [Fact]
    public async Task ShouldReportCancellationForwardedWhenFitzForwardsCancellation()
    {
        var rpc = new InMemoryRpcClient();
        var serializer = TestJson.Serializer(typeof(RpcGetValue));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var worker = await rpc.RegisterWorkerAsync(RpcGetValueRoute, async (_, _, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        });
        var sender = new FitzRemoteRequestSender(rpc, serializer, serializer);
        using var cancellation = new CancellationTokenSource();
        var sending = sender.SendWithOutcomeAsync<RpcGetValue, int>(
            new RpcGetValue(), new RequestRouteValues(), null, cancellation.Token).AsTask();

        await started.Task.WaitAsync(HangGuard);
        cancellation.Cancel();
        var outcome = await sending.WaitAsync(HangGuard);

        _ = Assert.IsType<RemoteRequestOutcome<int>.CancellationForwarded>(outcome);
    }

    /// <summary>A Fitz call with no final response and no observed cancellation has an unknown outcome.</summary>
    [Fact]
    public async Task ShouldReportUnknownOutcomeWhenRpcCallEndsWithoutFinalResponse()
    {
        var rpc = new InMemoryRpcClient();
        var serializer = TestJson.Serializer(typeof(RpcGetValue));
        await using var worker = await rpc.RegisterWorkerAsync(RpcGetValueRoute,
            (_, _, _) => ValueTask.CompletedTask);
        var sender = new FitzRemoteRequestSender(rpc, serializer, serializer);

        var outcome = await sender.SendWithOutcomeAsync<RpcGetValue, int>(
            new RpcGetValue(), new RequestRouteValues(), null);

        _ = Assert.IsType<RemoteRequestOutcome<int>.OutcomeUnknown>(outcome);
    }

    /// <summary>Transport failures after lazy RPC enumeration starts leave the remote outcome unknown.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldReportUnknownOutcomeWhenTransportFailsAfterDispatch(bool connectionDrop)
    {
        var serializer = TestJson.Serializer(typeof(RpcGetValue));
        var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception exception = connectionDrop
            ? new IOException("Connection dropped.")
            : new TimeoutException("RPC timed out.");
        var rpc = new ScriptedRpcClient(new RpcCall(ThrowAfterDispatch(dispatched, exception),
            Task.FromResult(RpcCancellationOutcome.ConnectionClosed)));
        var sender = new FitzRemoteRequestSender(rpc, serializer, serializer);

        var outcome = await sender.SendWithOutcomeAsync<RpcGetValue, int>(
            new RpcGetValue(), new RequestRouteValues(), null);

        _ = Assert.IsType<RemoteRequestOutcome<int>.OutcomeUnknown>(outcome);
        Assert.True(dispatched.Task.IsCompletedSuccessfully);
    }

    /// <summary>An operation cancellation unrelated to the caller token remains an exception.</summary>
    [Fact]
    public async Task ShouldThrowWhenRpcEnumerationIsCanceledWithoutCallerCancellation()
    {
        var serializer = TestJson.Serializer(typeof(RpcGetValue));
        var rpc = new ScriptedRpcClient(new RpcCall(ThrowAfterDispatch(new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously), new OperationCanceledException("Transport canceled.")),
            Task.FromResult(RpcCancellationOutcome.Unconfirmed)));
        var sender = new FitzRemoteRequestSender(rpc, serializer, serializer);

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            sender.SendWithOutcomeAsync<RpcGetValue, int>(new RpcGetValue(), new RequestRouteValues(), null).AsTask());

        Assert.Equal("Transport canceled.", exception.Message);
    }

    /// <summary>A response frame followed by interrupted enumeration does not prove completion.</summary>
    [Fact]
    public async Task ShouldReportUnknownOutcomeWhenCallerCancellationInterruptsAfterResponseFrame()
    {
        var serializer = TestJson.Serializer(typeof(RpcGetValue));
        var response = serializer.SerializeResult(Result<int>.Success(7));
        var responseReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rpc = new ScriptedRpcClient(new RpcCall(YieldResponseThenWait(response, responseReceived),
            Task.FromResult(RpcCancellationOutcome.Forwarded)));
        var sender = new FitzRemoteRequestSender(rpc, serializer, serializer);
        using var cancellation = new CancellationTokenSource();
        var sending = sender.SendWithOutcomeAsync<RpcGetValue, int>(
            new RpcGetValue(), new RequestRouteValues(), null, cancellation.Token).AsTask();

        await responseReceived.Task.WaitAsync(HangGuard);
        cancellation.Cancel();
        var outcome = await sending.WaitAsync(HangGuard);

        _ = Assert.IsType<RemoteRequestOutcome<int>.OutcomeUnknown>(outcome);
    }

    /// <summary>
    ///     Verifies that a no-result request sent over RPC reaches its handler.
    /// </summary>
    [Fact]
    public async Task ShouldRoundTripNoResultRequestOverRpc()
    {
        var rpc = new InMemoryRpcClient();
        var serializer = TestJson.Serializer(typeof(RpcChangeValue));
        var handler = new RpcChangeValueHandler();
        using var busHost = TestRequestBus.Create(rpcChangeValueHandler: handler);
        var server = new FitzRpcRequestServer(rpc, busHost.ScopeFactory);
        _ = await server.RegisterAsync<RpcChangeValue>();
        var sender = new FitzRemoteRequestSender(rpc, serializer, serializer);

        var result = await sender.SendAsync(new RpcChangeValue(99), new RequestRouteValues(), null);

        Assert.True(result.IsSuccess);
        Assert.Equal(99, handler.LastValue);
    }

    /// <summary>
    ///     Verifies that a request whose carried actor token fails re-validation on the receiving
    ///     side never reaches its handler — the same "always honor expiration" guarantee the queue
    ///     path has, enforced here at the point the RPC call is actually received, not just sent.
    /// </summary>
    [Fact]
    public async Task ShouldRejectRequestWhenActorTokenFailsRevalidationOnReceipt()
    {
        var rpc = new InMemoryRpcClient();
        var serializer = TestJson.Serializer(typeof(RpcChangeValue));
        var handler = new RpcChangeValueHandler();
        using var busHost =
            TestRequestBus.Create(rpcChangeValueHandler: handler, actorValidator: new RejectingActorValidator());
        var server = new FitzRpcRequestServer(rpc, busHost.ScopeFactory);
        _ = await server.RegisterAsync<RpcChangeValue>();
        var sender = new FitzRemoteRequestSender(rpc, serializer, serializer);

        var result = await sender.SendAsync(new RpcChangeValue(1), new RequestRouteValues(), "any-token");

        Assert.False(result.IsSuccess);
        Assert.Equal(RequestErrorKind.Unauthorized, result.Error.Kind);
        Assert.Equal("Token expired.", result.Error.Message);
        Assert.Null(handler.LastValue);
    }

    /// <summary>
    ///     Verifies that <c>RegisterRequestsAsync</c> — generated by
    ///     <c>PortiaServiceRegistrationGenerator</c> — registers a worker for every routed,
    ///     <see cref="ICallable" /> request in the compilation without a single manual
    ///     <see cref="FitzRpcRequestServer.RegisterAsync{TRequest}" /> call, the same way
    ///     Explicit component declarations cover DI registration.
    /// </summary>
    [Fact]
    public async Task ShouldDispatchRequestRegisteredEntirelyByGeneratedRpcWorkerRegistration()
    {
        var rpc = new InMemoryRpcClient();
        var serializer = TestJson.Serializer(typeof(RpcGetValue));
        using var busHost = TestRequestBus.Create();
        var server = new FitzRpcRequestServer(rpc, busHost.ScopeFactory);

        // No server.RegisterAsync<RpcGetValue, int>() call anywhere — this alone must cover it.
        await using var registration = await server.RegisterRequestsAsync();

        var sender = new FitzRemoteRequestSender(rpc, serializer, serializer);
        var result = await sender.SendAsync<RpcGetValue, int>(new RpcGetValue(), new RequestRouteValues(), null);

        Assert.True(result.IsSuccess);
        Assert.Equal(7, result.Value);
    }

    sealed class AlwaysValidActorValidator : IRequestActorValidator
    {
        public ValueTask<Result<ClaimsPrincipal>> ValidateAsync(string? token, CancellationToken ct = default) =>
            ValueTask.FromResult(Result<ClaimsPrincipal>.Success(RequestActor.System));
    }

    sealed class RejectingActorValidator : IRequestActorValidator
    {
        public ValueTask<Result<ClaimsPrincipal>> ValidateAsync(string? token, CancellationToken ct = default) =>
            ValueTask.FromResult(Result<ClaimsPrincipal>.Failure(
                new RequestError(RequestErrorKind.Unauthorized, "Token expired.")));
    }

    static async IAsyncEnumerable<RpcResponseFrame> ThrowAfterDispatch(TaskCompletionSource dispatched,
        Exception? exception)
    {
        dispatched.TrySetResult();
        await Task.Yield();
        if (exception is not null)
            throw exception;
        yield break;
    }

    static async IAsyncEnumerable<RpcResponseFrame> YieldResponseThenWait(ReadOnlyMemory<byte> response,
        TaskCompletionSource responseReceived, [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return new RpcResponseFrame(response, 0);
        responseReceived.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
    }

    sealed class SendTelemetryCapture : IDisposable
    {
        readonly ConcurrentQueue<Activity> _activities = new();
        readonly ConcurrentQueue<RecordedMeasurement> _measurements = new();
        readonly ActivityListener _activityListener;
        readonly MeterListener _meterListener;

        public SendTelemetryCapture()
        {
            _activityListener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == PortiaTelemetry.SourceName,
                Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStopped = _activities.Enqueue
            };
            ActivitySource.AddActivityListener(_activityListener);

            _meterListener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == PortiaTelemetry.SourceName)
                        listener.EnableMeasurementEvents(instrument);
                }
            };
            _meterListener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
                _measurements.Enqueue(new RecordedMeasurement(instrument.Name, tags.ToArray())));
            _meterListener.Start();
        }

        public void Dispose()
        {
            _meterListener.Dispose();
            _activityListener.Dispose();
        }

        public void AssertPreCancelledSends(string parentSpanId, params string[] requestNames)
        {
            var activities = _activities.Where(activity =>
                activity.OperationName == PortiaTelemetry.SendActivityName
                && activity.ParentSpanId.ToString() == parentSpanId
                && activity.GetTagItem("portia.request.name") is string requestName
                && requestNames.Contains(requestName)).ToArray();
            Assert.Equal(requestNames.Length, activities.Length);
            Assert.All(activities, activity => Assert.Equal("canceled", activity.GetTagItem("portia.outcome")));

            var durations = _measurements.Where(measurement =>
                measurement.Name == "portia.transport.operation.duration"
                && measurement.HasTag("portia.transport.name", "rpc")
                && measurement.HasTag("portia.operation", "send")
                && measurement.HasTag("portia.outcome", "canceled")).ToArray();
            Assert.True(durations.Length >= requestNames.Length,
                $"Expected at least {requestNames.Length} canceled send measurements, found {durations.Length}.");
        }

        sealed record RecordedMeasurement(string Name, KeyValuePair<string, object?>[] Tags)
        {
            public bool HasTag(string key, object value) => Tags.Any(tag =>
                tag.Key == key && Equals(tag.Value, value));
        }
    }

    sealed class ScriptedRpcClient(RpcCall call) : IRpcClient
    {
#pragma warning disable RS0026
        public RpcCall CallAsync(string route, ReadOnlyMemory<byte> body, CancellationToken ct = default)
        {
            _ = route;
            _ = body;
            _ = ct;
            return call;
        }

        public RpcCall CallAsync(string route, ReadOnlyMemory<byte> body, TimeSpan? timeout,
            CancellationToken ct = default)
        {
            _ = timeout;
            return CallAsync(route, body, ct);
        }
#pragma warning restore RS0026

        public Task<RpcWorkerRegistration> RegisterWorkerAsync(string pattern,
            Func<RpcRequest, IRpcResponseWriter, CancellationToken, ValueTask> handler,
            RpcWorkerOptions? options = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
