using System.Runtime.CompilerServices;
using Cntryl.Fitz.Abstractions.Domains.Notice;

namespace Cntryl.Portia;

/// <summary>
/// Receives request notifications published over Fitz notice fanout.
/// </summary>
/// <param name="notice">The Fitz notice client.</param>
/// <param name="serializer">The request serializer.</param>
/// <param name="route">The concrete Fitz notice route to subscribe to (<c>notice://realm/area/resource</c>).</param>
public sealed class FitzNoticeRequestConsumer(
    INoticeClient notice,
    IRequestDeserializer serializer,
    string route) : IRequestNotificationConsumer
{
    readonly INoticeClient _notice = notice ?? throw new ArgumentNullException(nameof(notice));
    readonly IRequestDeserializer _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
    readonly string _route = string.IsNullOrWhiteSpace(route)
        ? throw new ArgumentException("A notice route cannot be empty.", nameof(route))
        : route;

    /// <inheritdoc />
    public async IAsyncEnumerable<RequestNotification> ReadAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        // A Fitz notice subscription is itself a pull-based IAsyncEnumerable (as of
        // Cntryl.Fitz.Abstractions 0.1.1) — no callback bridging needed here anymore.
        await using var subscription = await _notice.SubscribeAsync(_route, ct).ConfigureAwait(false);

        await foreach (var message in subscription.WithCancellation(ct).ConfigureAwait(false))
        {
            var envelope = _serializer.DeserializeEnvelope(message.Body);
            var request = envelope.Request as IRequest
                ?? throw new InvalidOperationException(
                    "A Fitz notice message deserialized to a result-bearing request; only no-result requests can be published over notice.");
            yield return new RequestNotification(request, envelope.ActorToken, envelope.Metadata,
                new NoticeInvocation(message.Route), envelope.TraceContext, Name: envelope.Name);
        }
    }
}
