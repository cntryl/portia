using System.Runtime.CompilerServices;
using System.Text.Json;
using Cntryl.Fitz.Abstractions.Domains.Schedule;

namespace Cntryl.Portia;

/// <summary>
///     Receives requests as their Fitz schedule entries fire.
/// </summary>
/// <param name="schedule">The Fitz schedule client.</param>
/// <param name="serializer">The request serializer.</param>
/// <param name="route">The concrete Fitz schedule route to subscribe to (<c>schedule://realm/area/resource/operation</c>).</param>
public sealed class FitzScheduledRequestConsumer(
    IScheduleClient schedule,
    IRequestDeserializer serializer,
    string route) : IRequestNotificationConsumer
{
    readonly string _route = string.IsNullOrWhiteSpace(route)
        ? throw new ArgumentException("A schedule route cannot be empty.", nameof(route))
        : route;

    readonly IScheduleClient _schedule = schedule ?? throw new ArgumentNullException(nameof(schedule));
    readonly IRequestDeserializer _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));

    /// <inheritdoc />
    public async IAsyncEnumerable<RequestNotification> ReadAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // A Fitz schedule subscription is itself a pull-based IAsyncEnumerable (as of
        // Cntryl.Fitz.Abstractions 0.1.1) — no callback bridging needed here anymore.
        await using var subscription = await _schedule.SubscribeAsync(_route, ct).ConfigureAwait(false);

        await foreach (var notification in subscription.WithCancellation(ct).ConfigureAwait(false))
        {
            // An entry this consumer cannot translate — a legacy envelope, or one without a
            // complete system identity — is reported and dropped rather than ending the
            // subscription, which would stop every other schedule on this route too. The
            // recorded fault is what tells an operator the entry needs recreating.
            if (Translate(notification) is { } translated)
            {
                yield return translated;
            }
        }
    }

    RequestNotification? Translate(ScheduleNotification notification)
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
            return new RequestNotification(request, null,
                new RequestMetadata(Uuid.CreateVersion4(), envelope.Metadata.CorrelationId,
                    envelope.Metadata.RequestId),
                new ScheduleInvocation(notification.Route), envelope.TraceContext,
                RequestActor.CreateSystem(scheduled.SystemSubject, scheduled.SystemIssuer), envelope.Name);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PortiaTelemetry.RecordRunnerFault(nameof(FitzScheduledRequestConsumer), RunnerFaultStage.Validation, ex);
            return null;
        }
    }
}
