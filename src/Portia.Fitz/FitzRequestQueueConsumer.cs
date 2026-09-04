using System.Runtime.CompilerServices;
using Cntryl.Fitz.Abstractions.Domains.Queue;

namespace Cntryl.Portia;

/// <summary>
/// Reserves no-result requests off a Fitz queue for dispatch through the request bus.
/// </summary>
/// <param name="queue">The Fitz queue client.</param>
/// <param name="serializer">The request serializer.</param>
/// <param name="route">The concrete Fitz queue route to reserve from (<c>queue://realm/area/resource</c>).</param>
/// <param name="visibilityTimeoutSeconds">How long a reservation stays invisible to other consumers
/// before it is eligible for redelivery.</param>
/// <param name="maxItemsPerReserve">The maximum number of items requested per reserve call.</param>
/// <param name="waitMilliseconds">How long a reserve call may long-poll for items before returning empty.</param>
public sealed class FitzRequestQueueConsumer(
    IQueueClient queue,
    IRequestDeserializer serializer,
    string route,
    ulong visibilityTimeoutSeconds = 30,
    int maxItemsPerReserve = 16,
    int waitMilliseconds = 5_000) : IRequestQueueConsumer
{
    readonly IQueueClient _queue = queue ?? throw new ArgumentNullException(nameof(queue));
    readonly IRequestDeserializer _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
    readonly string _route = string.IsNullOrWhiteSpace(route)
        ? throw new ArgumentException("A queue route cannot be empty.", nameof(route))
        : route;

    /// <inheritdoc />
    public async IAsyncEnumerable<IQueuedRequest> ReadAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var items = await _queue
                .ReserveAsync(_route, visibilityTimeoutSeconds, maxItemsPerReserve, waitMilliseconds, ct)
                .ConfigureAwait(false);

            foreach (var item in items)
                yield return new FitzQueuedRequest(item, _serializer);
        }
    }

    sealed class FitzQueuedRequest : IQueuedRequest
    {
        readonly IQueueReservedItem _item;

        public FitzQueuedRequest(IQueueReservedItem item, IRequestDeserializer serializer)
        {
            _item = item;
            var (request, actorToken) = serializer.DeserializeRequest(item.Body);
            Request = request as IRequest
                ?? throw new InvalidOperationException(
                    "A Fitz queue item deserialized to a result-bearing request; only no-result requests can be queued.");
            ActorToken = actorToken;
        }

        public IRequest Request { get; }

        public string? ActorToken { get; }

        public uint Attempt => _item.Attempt;

        public ValueTask CompleteAsync(CancellationToken ct = default) => new(_item.CompleteAsync(ct));

        // Fitz has no explicit abandon/nack on a reserved item — an incomplete reservation
        // simply becomes redeliverable once its visibility timeout elapses.
        public ValueTask AbandonAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
    }
}
