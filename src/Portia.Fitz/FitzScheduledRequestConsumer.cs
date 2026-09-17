using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cntryl.Portia;

/// <summary>
///     Receives requests as their Fitz schedule entries fire.
/// </summary>
/// <param name="schedule">The Fitz schedule client.</param>
/// <param name="serializer">The request serializer.</param>
/// <param name="route">The concrete Fitz schedule route to subscribe to (<c>schedule://realm/area/resource/operation</c>).</param>
/// <param name="catalog">Validates the request's declared transport capability.</param>
/// <param name="actorValidator">Maps the untrusted scheduled identity assertion to an approved principal.</param>
/// <param name="logger">Reports a delivery that cannot be translated.</param>
public sealed class FitzScheduledRequestConsumer(
    IScheduleClient schedule,
    IRequestDeserializer serializer,
    string route,
    RequestTransportCatalog catalog,
    IScheduledRequestActorValidator actorValidator,
    ILogger<FitzScheduledRequestConsumer>? logger = null) : IRequestNotificationConsumer
{
    const int ValidationAttempts = 3;
    const int MaxDeferredRetries = 32;

    readonly IScheduledRequestActorValidator _actorValidator =
        actorValidator ?? throw new ArgumentNullException(nameof(actorValidator));

    readonly RequestTransportCatalog _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    readonly TimeProvider _clock = TimeProvider.System;

    readonly string _route = string.IsNullOrWhiteSpace(route)
        ? throw new ArgumentException("A schedule route cannot be empty.", nameof(route))
        : route;

    readonly IScheduleClient _schedule = schedule ?? throw new ArgumentNullException(nameof(schedule));
    readonly IServiceScopeFactory? _scopes;
    readonly IRequestDeserializer _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));

    internal FitzScheduledRequestConsumer(IScheduleClient schedule, IRequestDeserializer serializer, string route,
        RequestTransportCatalog catalog, IServiceScopeFactory scopes, TimeProvider timeProvider,
        ILogger<FitzScheduledRequestConsumer>? logger = null)
        : this(schedule, serializer, route, catalog, ScopedValidatorSentinel.Instance, logger)
    {
        _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        _clock = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RequestNotification> ReadAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Firings are read and first validated in route order, but a firing whose validator fails
        // transiently waits out its backoff off the read loop, so one slow identity check never delays
        // the healthy firings behind it. Capacity 1 keeps reading paced by dispatch as before.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var output = Channel.CreateBounded<RequestNotification>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });
        var pump = PumpAsync(output.Writer, stop);
        try
        {
            await foreach (var notification in output.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return notification;
            }
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await pump.ConfigureAwait(false);
        }
    }

    async Task PumpAsync(ChannelWriter<RequestNotification> output, CancellationTokenSource stop)
    {
        var ct = stop.Token;
        var retries = new List<Task>();
        using var retrySlots = new SemaphoreSlim(MaxDeferredRetries, MaxDeferredRetries);
        try
        {
            // A Fitz schedule subscription is itself a pull-based IAsyncEnumerable (as of
            // Cntryl.Fitz.Abstractions) — no callback bridging needed here anymore.
            await using (var subscription = await _schedule.SubscribeAsync(_route, ct).ConfigureAwait(false))
            {
                await foreach (var notification in subscription.WithCancellation(ct).ConfigureAwait(false))
                {
                    // An entry this consumer cannot translate — a legacy envelope, or one without a
                    // complete system identity — is reported and dropped rather than ending the
                    // subscription, which would stop every other schedule on this route too. The
                    // recorded fault is what tells an operator the entry needs recreating.
                    if (Translate(notification) is not { } firing)
                    {
                        continue;
                    }

                    var attempt = await TryValidateActorAsync(firing, ct).ConfigureAwait(false);
                    if (attempt.Actor is { } actor)
                    {
                        await output.WriteAsync(firing.Deliver(actor), ct).ConfigureAwait(false);
                    }
                    else if (attempt.Retry)
                    {
                        // Bounded so an unavailable validator during a burst of firings applies
                        // backpressure to the route instead of accumulating unbounded pending retries.
                        await retrySlots.WaitAsync(ct).ConfigureAwait(false);
                        _ = retries.RemoveAll(retry => retry.IsCompleted);
                        retries.Add(RetryAsync(firing, output, retrySlots, ct));
                    }
                }
            }

            await Task.WhenAll(retries).ConfigureAwait(false);
            _ = output.TryComplete();
        }
        catch (Exception ex)
        {
            await stop.CancelAsync().ConfigureAwait(false);
            try
            {
                await Task.WhenAll(retries).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Pending retries only end by cancellation here; the pump's own failure is reported.
            }

            _ = output.TryComplete(ex);
        }
    }

    async Task RetryAsync(ScheduledFiring firing, ChannelWriter<RequestNotification> output,
        SemaphoreSlim retrySlots, CancellationToken ct)
    {
        try
        {
            for (var attempt = 2; attempt <= ValidationAttempts; attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt - 1), _clock, ct).ConfigureAwait(false);
                var result = await TryValidateActorAsync(firing, ct).ConfigureAwait(false);
                if (result.Actor is { } actor)
                {
                    await output.WriteAsync(firing.Deliver(actor), ct).ConfigureAwait(false);
                    return;
                }

                if (!result.Retry)
                {
                    return;
                }
            }

            RecordLost();
        }
        finally
        {
            _ = retrySlots.Release();
        }
    }

    ScheduledFiring? Translate(ScheduleNotification notification)
    {
        try
        {
            using (var document = JsonDocument.Parse(notification.Payload))
            {
                if (document.RootElement.TryGetProperty("contract", out _))
                {
                    throw new LegacyScheduledRequestException(notification.Route);
                }
            }

            var scheduled = JsonSerializer.Deserialize(notification.Payload.Span,
                                FitzJsonContext.Default.FitzScheduledRequestEnvelope)
                            ?? throw new InvalidOperationException("A fired schedule envelope deserialized to null.");
            if (scheduled.Version != 1 || string.IsNullOrWhiteSpace(scheduled.SystemSubject)
                                       || string.IsNullOrWhiteSpace(scheduled.SystemIssuer))
            {
                throw new InvalidOperationException("A fired schedule has an invalid system identity envelope.");
            }

            var envelope = _serializer.DeserializeEnvelope(scheduled.RequestEnvelope);
            if (envelope.ActorToken is not null)
            {
                throw new InvalidOperationException(
                    "A durable schedule request envelope cannot contain an actor token.");
            }

            var request = envelope.Request as IRequest
                          ?? throw new InvalidOperationException(
                              "A fired Fitz schedule entry deserialized to a result-bearing request; only no-result requests can be scheduled.");
            if (!_catalog.Get(request.GetType()).Transports.Contains(RequestTransportId.Schedule))
                throw new InvalidRequestTransportException(request, RequestTransportId.Schedule);
            return new ScheduledFiring(notification.Route, scheduled.SystemSubject, scheduled.SystemIssuer, request,
                envelope);
        }
        catch (Exception)
        {
            RecordLost();
            return null;
        }
    }

    // One validator call in a fresh scope. A definite rejection loses the firing; a thrown or transient
    // failure is recorded and left to the caller to retry.
    async ValueTask<ValidationAttempt> TryValidateActorAsync(ScheduledFiring firing, CancellationToken ct)
    {
        try
        {
            Result<ClaimsPrincipal> result;
            if (_scopes is null)
            {
                result = await _actorValidator.ValidateAsync(firing.Route, firing.Subject, firing.Issuer, ct)
                    .ConfigureAwait(false);
            }
            else
            {
                await using var scope = _scopes.CreateAsyncScope();
                var validator = scope.ServiceProvider.GetRequiredService<IScheduledRequestActorValidator>();
                result = await validator.ValidateAsync(firing.Route, firing.Subject, firing.Issuer, ct)
                    .ConfigureAwait(false);
            }

            if (result.IsSuccess)
                return new ValidationAttempt(result.Value, false);
            if (!result.Error!.IsTransient)
            {
                RecordLost();
                return new ValidationAttempt(null, false);
            }

            PortiaTelemetry.RecordRunnerFault(nameof(FitzScheduledRequestConsumer), RunnerFaultStage.Validation,
                new ScheduledActorValidationException(result.Error), logger);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            PortiaTelemetry.RecordRunnerFault(nameof(FitzScheduledRequestConsumer), RunnerFaultStage.Validation,
                ex, logger);
        }

        return new ValidationAttempt(null, true);
    }

    void RecordLost()
    {
        PortiaTelemetry.RecordLostDelivery("unknown", "schedule", logger);
        PortiaTelemetry.RecordDelivery("unknown", "schedule", RequestDeliveryOutcome.Lost);
    }

    readonly record struct ValidationAttempt(ClaimsPrincipal? Actor, bool Retry);

    sealed record ScheduledFiring(string Route, string Subject, string Issuer, IRequest Request,
        DeserializedRequest Envelope)
    {
        public RequestNotification Deliver(ClaimsPrincipal actor) =>
            new(Request, null,
                new RequestMetadata(Uuid.CreateVersion4(), Envelope.Metadata.CorrelationId,
                    Envelope.Metadata.RequestId),
                new ScheduleInvocation(Route) { MessagingSystem = "fitz" }, Envelope.TraceContext,
                actor, Envelope.Name);
    }

    sealed class ScopedValidatorSentinel : IScheduledRequestActorValidator
    {
        public static ScopedValidatorSentinel Instance { get; } = new();

        public ValueTask<Result<ClaimsPrincipal>> ValidateAsync(
            string route, string subject, string issuer, CancellationToken ct = default) =>
            throw new InvalidOperationException("The hosted scheduled validator must be resolved from a scope.");
    }

    sealed class ScheduledActorValidationException(RequestError error) : Exception(
        $"Scheduled actor validation transiently failed with kind '{error.Kind}': {error.Message}");
}
