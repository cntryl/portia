using System.Runtime.CompilerServices;
using Cntryl.Fitz.Abstractions.Domains.Schedule;

namespace Cntryl.Portia;

/// <summary>
/// Receives requests as their Fitz schedule entries fire.
/// </summary>
/// <param name="schedule">The Fitz schedule client.</param>
/// <param name="serializer">The request serializer.</param>
/// <param name="route">The concrete Fitz schedule route to subscribe to (<c>schedule://realm/area/resource/operation</c>).</param>
public sealed class FitzScheduledRequestConsumer(
    IScheduleClient schedule,
    IRequestDeserializer serializer,
    string route) : IRequestNotificationConsumer
{
    readonly IScheduleClient _schedule = schedule ?? throw new ArgumentNullException(nameof(schedule));
    readonly IRequestDeserializer _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
    readonly string _route = string.IsNullOrWhiteSpace(route)
        ? throw new ArgumentException("A schedule route cannot be empty.", nameof(route))
        : route;

    /// <inheritdoc />
    public async IAsyncEnumerable<RequestNotification> ReadAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        // A Fitz schedule subscription is itself a pull-based IAsyncEnumerable (as of
        // Cntryl.Fitz.Abstractions 0.1.1) — no callback bridging needed here anymore.
        await using var subscription = await _schedule.SubscribeAsync(_route, ct).ConfigureAwait(false);

        await foreach (var notification in subscription.WithCancellation(ct).ConfigureAwait(false))
        {
            var envelope = _serializer.DeserializeEnvelope(notification.Payload);
            var request = envelope.Request as IRequest
                ?? throw new InvalidOperationException(
                    "A fired Fitz schedule entry deserialized to a result-bearing request; only no-result requests can be scheduled.");
            yield return new RequestNotification(request, envelope.ActorToken, new RequestMetadata(Uuid.CreateVersion4(), envelope.Metadata.CorrelationId, envelope.Metadata.RequestId), new ScheduleInvocation(notification.Route));
        }
    }
}
