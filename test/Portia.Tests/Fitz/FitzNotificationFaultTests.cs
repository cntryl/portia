using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

/// <summary>
///     A fired schedule entry a consumer cannot translate is dropped rather than dispatched, so the
///     runner fault it records is what tells an operator the route has a broken entry.
/// </summary>
[Collection(TelemetryTestGroup.Name)]
public sealed class FitzNotificationFaultTests
{
    /// <summary>Transient validator results and exceptions retry the same firing in fresh scopes.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldRetryTransientScheduledValidationWithInjectedClockAndFreshScopes(bool throws)
    {
        var serializer = TestJson.Serializer(typeof(UniversalAction));
        var payload = ScheduledPayload(serializer);
        var tracker = new ValidatorTracker { ThrowFirst = throws, FailFirstTransiently = !throws };
        var services = new ServiceCollection();
        _ = services.AddSingleton(tracker);
        _ = services.AddScoped<IScheduledRequestActorValidator, ScopedValidator>();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var clock = new ManualTestClock();
        var consumer = new FitzScheduledRequestConsumer(new OnePayloadScheduleClient(payload), serializer,
            "schedule://test/shared/action/run",
            TestJson.Catalog(RequestTransportId.Schedule, typeof(UniversalAction)),
            provider.GetRequiredService<IServiceScopeFactory>(), clock);
        await using var enumerator = consumer.ReadAsync().GetAsyncEnumerator();

        var move = enumerator.MoveNextAsync().AsTask();
        Assert.Equal(TimeSpan.FromSeconds(1), await clock.WaitForDelayAsync());
        Assert.Equal(1, tracker.Disposed);
        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.True(await move);
        _ = Assert.IsType<UniversalAction>(enumerator.Current.Request);
        Assert.Equal(2, tracker.Instances.Count);
        Assert.Equal(2, tracker.Instances.Distinct().Count());
        Assert.Equal(2, tracker.Disposed);
    }

    /// <summary>A validator retry delay remains cancellable and disposes its failed attempt scope.</summary>
    [Fact]
    public async Task ShouldCancelScheduledValidatorRetry()
    {
        var serializer = TestJson.Serializer(typeof(UniversalAction));
        var tracker = new ValidatorTracker { AlwaysTransient = true };
        var services = new ServiceCollection();
        _ = services.AddSingleton(tracker);
        _ = services.AddScoped<IScheduledRequestActorValidator, ScopedValidator>();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var clock = new ManualTestClock();
        var consumer = new FitzScheduledRequestConsumer(new OnePayloadScheduleClient(ScheduledPayload(serializer)),
            serializer, "schedule://test/shared/action/run",
            TestJson.Catalog(RequestTransportId.Schedule, typeof(UniversalAction)),
            provider.GetRequiredService<IServiceScopeFactory>(), clock);
        using var cancellation = new CancellationTokenSource();
        await using var enumerator = consumer.ReadAsync(cancellation.Token).GetAsyncEnumerator();

        var move = enumerator.MoveNextAsync().AsTask();
        Assert.Equal(TimeSpan.FromSeconds(1), await clock.WaitForDelayAsync());
        await cancellation.CancelAsync();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move);
        Assert.Equal(1, tracker.Disposed);
    }

    /// <summary>A transient validator outage longer than three attempts must not discard a live firing.</summary>
    [Fact]
    public async Task ShouldKeepRetryingScheduledValidationUntilTheValidatorRecovers()
    {
        var serializer = TestJson.Serializer(typeof(UniversalAction));
        var tracker = new ValidatorTracker { TransientAttempts = 3 };
        var services = new ServiceCollection();
        _ = services.AddSingleton(tracker);
        _ = services.AddScoped<IScheduledRequestActorValidator, ScopedValidator>();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var clock = new ManualTestClock();
        var consumer = new FitzScheduledRequestConsumer(new OnePayloadScheduleClient(ScheduledPayload(serializer)),
            serializer, "schedule://test/shared/action/run",
            TestJson.Catalog(RequestTransportId.Schedule, typeof(UniversalAction)),
            provider.GetRequiredService<IServiceScopeFactory>(), clock);
        await using var enumerator = consumer.ReadAsync().GetAsyncEnumerator();

        var move = enumerator.MoveNextAsync().AsTask();
        Assert.Equal(TimeSpan.FromSeconds(1), await clock.WaitForDelayAsync());
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.FromSeconds(2), await clock.WaitForDelayAsync());
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.FromSeconds(4), await clock.WaitForDelayAsync());
        Assert.False(move.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(4));

        Assert.True(await move);
        Assert.Equal(4, tracker.Attempts);
        Assert.Equal(4, tracker.Disposed);
    }

    /// <summary>Long outages cap retry delay and cancellation releases the pending firing.</summary>
    [Fact]
    public async Task ShouldCapScheduledValidationBackoffAndCancel()
    {
        var serializer = TestJson.Serializer(typeof(UniversalAction));
        var tracker = new ValidatorTracker { AlwaysTransient = true };
        var services = new ServiceCollection();
        _ = services.AddSingleton(tracker);
        _ = services.AddScoped<IScheduledRequestActorValidator, ScopedValidator>();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var clock = new ManualTestClock();
        var consumer = new FitzScheduledRequestConsumer(new OnePayloadScheduleClient(ScheduledPayload(serializer)),
            serializer, "schedule://test/shared/action/run",
            TestJson.Catalog(RequestTransportId.Schedule, typeof(UniversalAction)),
            provider.GetRequiredService<IServiceScopeFactory>(), clock);
        using var cancellation = new CancellationTokenSource();
        await using var enumerator = consumer.ReadAsync(cancellation.Token).GetAsyncEnumerator();

        var move = enumerator.MoveNextAsync().AsTask();
        foreach (var seconds in new[] { 1, 2, 4, 8, 16 })
        {
            Assert.Equal(TimeSpan.FromSeconds(seconds), await clock.WaitForDelayAsync());
            clock.Advance(TimeSpan.FromSeconds(seconds));
        }
        Assert.Equal(TimeSpan.FromSeconds(30), await clock.WaitForDelayAsync());
        Assert.False(move.IsCompleted);
        await cancellation.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move);
        Assert.Equal(tracker.Attempts, tracker.Disposed);
    }

    /// <summary>
    ///     A firing waiting out a validator backoff does not hold the route: the next firing is validated
    ///     and delivered first, and the earlier firing is still delivered once its retry succeeds.
    /// </summary>
    [Fact]
    public async Task ShouldDeliverLaterScheduledFiringWhileEarlierFiringWaitsToRetryValidation()
    {
        var serializer = TestJson.Serializer(typeof(UniversalAction));
        var tracker = new ValidatorTracker { FailFirstTransiently = true };
        var services = new ServiceCollection();
        _ = services.AddSingleton(tracker);
        _ = services.AddScoped<IScheduledRequestActorValidator, ScopedValidator>();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var clock = new ManualTestClock();
        var consumer = new FitzScheduledRequestConsumer(new TwoPayloadScheduleClient(ScheduledPayload(serializer)),
            serializer, "schedule://test/shared/action/run",
            TestJson.Catalog(RequestTransportId.Schedule, typeof(UniversalAction)),
            provider.GetRequiredService<IServiceScopeFactory>(), clock);
        await using var enumerator = consumer.ReadAsync().GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(2, tracker.Attempts);
        Assert.Equal(TimeSpan.FromSeconds(1), await clock.WaitForDelayAsync());

        var move = enumerator.MoveNextAsync().AsTask();
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(await move);
        Assert.Equal(3, tracker.Attempts);
        Assert.False(await enumerator.MoveNextAsync());
    }

    /// <summary>A malformed nested request is dropped before actor validation can enter its retry loop.</summary>
    [Fact]
    public async Task ShouldNotValidateActorForMalformedScheduledRequestEnvelope()
    {
        var serializer = TestJson.Serializer(typeof(UniversalAction));
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new FitzScheduledRequestEnvelope(1, "scheduler", "portia", [123]),
            FitzJsonContext.Default.FitzScheduledRequestEnvelope);
        var tracker = new ValidatorTracker { AlwaysTransient = true };
        var services = new ServiceCollection();
        _ = services.AddSingleton(tracker);
        _ = services.AddScoped<IScheduledRequestActorValidator, ScopedValidator>();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var consumer = new FitzScheduledRequestConsumer(new OnePayloadScheduleClient(payload), serializer,
            "schedule://test/shared/action/run",
            TestJson.Catalog(RequestTransportId.Schedule, typeof(UniversalAction)),
            provider.GetRequiredService<IServiceScopeFactory>(), new ManualTestClock());

        await using var enumerator = consumer.ReadAsync().GetAsyncEnumerator();
        Assert.False(await enumerator.MoveNextAsync());
        Assert.Equal(0, tracker.Attempts);
    }

    /// <summary>A definite validator rejection is dropped without scheduling a retry.</summary>
    [Fact]
    public async Task ShouldDropNonTransientScheduledValidatorRejection()
    {
        var serializer = TestJson.Serializer(typeof(UniversalAction));
        var tracker = new ValidatorTracker { AlwaysRejected = true };
        var services = new ServiceCollection();
        _ = services.AddSingleton(tracker);
        _ = services.AddScoped<IScheduledRequestActorValidator, ScopedValidator>();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var consumer = new FitzScheduledRequestConsumer(new OnePayloadScheduleClient(ScheduledPayload(serializer)),
            serializer, "schedule://test/shared/action/run",
            TestJson.Catalog(RequestTransportId.Schedule, typeof(UniversalAction)),
            provider.GetRequiredService<IServiceScopeFactory>(), new ManualTestClock());

        await using var enumerator = consumer.ReadAsync().GetAsyncEnumerator();

        Assert.False(await enumerator.MoveNextAsync());
        Assert.Equal(1, tracker.Disposed);
    }

    /// <summary>Hosted schedule validation gets one disposed dependency-injection scope per firing.</summary>
    [Fact]
    public async Task ShouldResolveAndDisposeDistinctScopedValidatorsForScheduledNotifications()
    {
        var serializer = TestJson.Serializer(typeof(UniversalAction));
        var request = serializer.Serialize(new UniversalAction(1), null, RequestMetadata.Create(), null);
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new FitzScheduledRequestEnvelope(1, "scheduler", "portia", request.ToArray()),
            FitzJsonContext.Default.FitzScheduledRequestEnvelope);
        var tracker = new ValidatorTracker();
        var services = new ServiceCollection();
        _ = services.AddSingleton(tracker);
        _ = services.AddScoped<IScheduledRequestActorValidator, ScopedValidator>();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var consumer = new FitzScheduledRequestConsumer(new TwoPayloadScheduleClient(payload), serializer,
            "schedule://test/shared/action/run",
            TestJson.Catalog(RequestTransportId.Schedule, typeof(UniversalAction)),
            provider.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);

        var delivered = 0;
        await foreach (var _ in consumer.ReadAsync())
            delivered++;

        Assert.Equal(2, delivered);
        Assert.Equal(2, tracker.Instances.Count);
        Assert.Equal(2, tracker.Instances.Distinct().Count());
        Assert.Equal(2, tracker.Disposed);
    }

    /// <summary>Verifies a pre-envelope schedule entry is dropped and counted as a validation fault.</summary>
    [Fact]
    public async Task ShouldReportLegacyScheduleEntriesAsAValidationFault()
    {
        var serializer = TestJson.Serializer(typeof(UniversalAction));
        var legacy = serializer.Serialize(new UniversalAction(1), "legacy-bearer-token", RequestMetadata.Create(),
            null);

        Assert.Equal([("unknown", "schedule", "lost")], await DrainAsync(serializer, legacy));
    }

    /// <summary>Verifies an envelope without a complete system identity is dropped and counted the same way.</summary>
    [Fact]
    public async Task ShouldReportAnIncompleteSystemIdentityAsAValidationFault()
    {
        var serializer = TestJson.Serializer(typeof(UniversalAction));
        var request = serializer.Serialize(new UniversalAction(1), null, RequestMetadata.Create(), null);
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new FitzScheduledRequestEnvelope(1, "   ", "Portia", request.ToArray()),
            FitzJsonContext.Default.FitzScheduledRequestEnvelope);

        Assert.Equal([("unknown", "schedule", "lost")], await DrainAsync(serializer, payload));
    }

    /// <summary>
    ///     Verifies the legacy entry's diagnostic still names the route and the action an operator
    ///     has to take, now that it is reported rather than thrown at the caller.
    /// </summary>
    [Fact]
    public void ShouldNameTheRouteAndRemedyInTheLegacyScheduleDiagnostic()
    {
        var exception = new LegacyScheduledRequestException("schedule://test/shared/action/run");

        Assert.Contains("Cancel and recreate", exception.Message, StringComparison.Ordinal);
        Assert.Contains("schedule://test/shared/action/run", exception.Message, StringComparison.Ordinal);
    }

    static async Task<List<(string Request, string Transport, string Outcome)>> DrainAsync(
        IRequestDeserializer serializer,
        ReadOnlyMemory<byte> payload)
    {
        var lost = new List<(string, string, string)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == PortiaTelemetry.SourceName &&
                instrument.Name == "portia.request.delivery.count")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            var values = tags.ToArray();
            lock (lost)
                lost.Add(((string)values[0].Value!, (string)values[1].Value!, (string)values[2].Value!));
        });
        listener.Start();
        var consumer = new FitzScheduledRequestConsumer(new OnePayloadScheduleClient(payload), serializer,
            "schedule://test/shared/action/run",
            TestJson.Catalog(RequestTransportId.Schedule, typeof(UniversalAction)),
            new AllowScheduledRequestActorValidator());

        await foreach (var _ in consumer.ReadAsync())
            Assert.Fail("A schedule entry that cannot be translated must not be dispatched.");

        listener.RecordObservableInstruments();
        lock (lost)
            return [.. lost];
    }

    static ReadOnlyMemory<byte> ScheduledPayload(JsonRequestSerializer serializer)
    {
        var request = serializer.Serialize(new UniversalAction(1), null, RequestMetadata.Create(), null);
        return JsonSerializer.SerializeToUtf8Bytes(
            new FitzScheduledRequestEnvelope(1, "scheduler", "portia", request.ToArray()),
            FitzJsonContext.Default.FitzScheduledRequestEnvelope);
    }

    sealed class OnePayloadScheduleClient(ReadOnlyMemory<byte> payload) : IScheduleClient
    {
        public Task<ScheduleSubscription> SubscribeAsync(string selector, CancellationToken ct = default) =>
            Task.FromResult(new ScheduleSubscription(selector, Notifications(selector, ct),
                _ => ValueTask.CompletedTask, Task.CompletedTask));

        public Task<string?> CreateAsync(string route, string cron, ScheduleDeliveryMode mode,
            ReadOnlyMemory<byte> body, CancellationToken ct = default) => throw new NotSupportedException();

        public Task CancelAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<ScheduleListPage> ListAsync(ulong? offset, ulong? limit, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ScheduleEntry>> ListBySelectorAsync(string selector,
            CancellationToken ct = default) => throw new NotSupportedException();

        async IAsyncEnumerable<ScheduleNotification> Notifications(string route,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            yield return new ScheduleNotification(route, payload);
        }
    }

    sealed class TwoPayloadScheduleClient(ReadOnlyMemory<byte> payload) : IScheduleClient
    {
        public Task<ScheduleSubscription> SubscribeAsync(string selector, CancellationToken ct = default) =>
            Task.FromResult(new ScheduleSubscription(selector, Notifications(selector, ct),
                _ => ValueTask.CompletedTask, Task.CompletedTask));

        public Task<string?> CreateAsync(string route, string cron, ScheduleDeliveryMode mode,
            ReadOnlyMemory<byte> body, CancellationToken ct = default) => throw new NotSupportedException();

        public Task CancelAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<ScheduleListPage> ListAsync(ulong? offset, ulong? limit, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ScheduleEntry>> ListBySelectorAsync(string selector,
            CancellationToken ct = default) => throw new NotSupportedException();

        async IAsyncEnumerable<ScheduleNotification> Notifications(string route,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            yield return new ScheduleNotification(route, payload);
            yield return new ScheduleNotification(route, payload);
        }
    }

    // Deferred validator retries run concurrently with the route's read loop, so every count is guarded.
    sealed class ValidatorTracker
    {
        readonly Lock _gate = new();
        readonly List<Guid> _instances = [];
        int _attempts;
        int _disposed;

        public List<Guid> Instances
        {
            get
            {
                lock (_gate)
                    return [.. _instances];
            }
        }

        public int Disposed
        {
            get
            {
                lock (_gate)
                    return _disposed;
            }
        }

        public int Attempts
        {
            get
            {
                lock (_gate)
                    return _attempts;
            }
        }

        public bool ThrowFirst { get; init; }
        public bool FailFirstTransiently { get; init; }
        public bool AlwaysTransient { get; init; }
        public int TransientAttempts { get; init; }
        public bool AlwaysRejected { get; init; }

        public int RecordAttempt(Guid instance)
        {
            lock (_gate)
            {
                _instances.Add(instance);
                return ++_attempts;
            }
        }

        public void RecordDisposed()
        {
            lock (_gate)
                _disposed++;
        }
    }

    sealed class ScopedValidator(ValidatorTracker tracker) : IScheduledRequestActorValidator, IDisposable
    {
        readonly Guid _id = Guid.NewGuid();

        public void Dispose() => tracker.RecordDisposed();

        public ValueTask<Result<ClaimsPrincipal>> ValidateAsync(string route, string subject, string issuer,
            CancellationToken ct = default)
        {
            var attempt = tracker.RecordAttempt(_id);
            if (tracker.ThrowFirst && attempt == 1)
                throw new IOException("identity provider unavailable");
            if (tracker.AlwaysTransient || attempt <= tracker.TransientAttempts ||
                (tracker.FailFirstTransiently && attempt == 1))
                return ValueTask.FromResult(Result<ClaimsPrincipal>.Failure(
                    new RequestError(RequestErrorKind.Conflict, "identity provider unavailable", true)));
            if (tracker.AlwaysRejected)
                return ValueTask.FromResult(Result<ClaimsPrincipal>.Failure(
                    new RequestError(RequestErrorKind.Unauthorized, "identity rejected")));
            return ValueTask.FromResult(Result<ClaimsPrincipal>.Success(RequestActor.CreateSystem(subject, issuer)));
        }
    }
}
