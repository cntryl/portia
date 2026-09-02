using Cntryl.Fitz.Abstractions.Domains.Notice;

namespace Cntryl.Portia;

/// <summary>
/// Publishes requests over Fitz live (ephemeral) notice fanout.
/// </summary>
/// <param name="notice">The Fitz notice client.</param>
/// <param name="serializer">The request serializer.</param>
public sealed class FitzNoticeRequestSender(INoticeClient notice, IRequestSerializer serializer) : INoticeRequestSender
{
    readonly INoticeClient _notice = notice ?? throw new ArgumentNullException(nameof(notice));
    readonly IRequestSerializer _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));

    /// <inheritdoc />
    public async ValueTask PublishAsync<TRequest>(TRequest request, RequestRouteValues routeValues, string? actorToken, CancellationToken ct = default)
        where TRequest : IRequest, INotifiable
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(routeValues);

        var route = FitzRouting.ResolveNoticeRoute(request, routeValues);
        var body = _serializer.Serialize(request, actorToken);
        await _notice.PublishAsync(route, body, ct).ConfigureAwait(false);
    }
}
