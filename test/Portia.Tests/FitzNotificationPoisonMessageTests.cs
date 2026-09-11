using System.Runtime.CompilerServices;
using System.Text;
using Cntryl.Fitz.Abstractions.Domains.Notice;
using Cntryl.Fitz.Abstractions.Domains.Schedule;

namespace Cntryl.Portia;

/// <summary>
///     Covers what a Fitz notice or schedule consumer does with a payload it cannot deserialize.
///     Neither transport redelivers, so a malformed message is lost either way — but losing it must
///     not also end the enumeration and take every later delivery with it.
/// </summary>
public sealed class FitzNotificationPoisonMessageTests
{
    /// <summary>
    ///     Verifies a notice whose body is not a Portia envelope is skipped and the next notice on
    ///     the same subscription still arrives.
    /// </summary>
    [Fact]
    public async Task ShouldSkipAnUndeserializableNoticeAndKeepReading()
    {
        var serializer = TestJson.Serializer(typeof(UniversalAction));
        var consumer = new FitzNoticeRequestConsumer(
            new ScriptedNoticeClient([Encoding.UTF8.GetBytes("not-an-envelope"), Envelope(serializer)]),
            serializer, "notice://test/shared/action");

        var delivered = await ReadAllAsync(consumer);

        _ = Assert.Single(delivered);
        _ = Assert.IsType<UniversalAction>(delivered[0].Request);
    }

    /// <summary>
    ///     Verifies the same for a fired schedule entry, whose envelope has a second layer that can
    ///     fail independently of the request payload inside it.
    /// </summary>
    [Fact]
    public async Task ShouldSkipAnUndeserializableScheduleEntryAndKeepReading()
    {
        var serializer = TestJson.Serializer(typeof(UniversalAction));
        var consumer = new FitzScheduledRequestConsumer(
            new ScriptedScheduleClient([Encoding.UTF8.GetBytes("{}"), ScheduleEnvelope(serializer)]),
            serializer, "schedule://test/shared/action/run");

        var delivered = await ReadAllAsync(consumer);

        _ = Assert.Single(delivered);
        _ = Assert.IsType<UniversalAction>(delivered[0].Request);
    }

    static async Task<List<RequestNotification>> ReadAllAsync(IRequestNotificationConsumer consumer)
    {
        var delivered = new List<RequestNotification>();
        await foreach (var notification in consumer.ReadAsync())
            delivered.Add(notification);
        return delivered;
    }

    static ReadOnlyMemory<byte> Envelope(JsonRequestSerializer serializer) =>
        serializer.Serialize(new UniversalAction(1), null, RequestMetadata.Create(), null);

    static ReadOnlyMemory<byte> ScheduleEnvelope(JsonRequestSerializer serializer) =>
        System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            new FitzScheduledRequestEnvelope(1, "portia:system", "Portia", Envelope(serializer).ToArray()),
            FitzJsonContext.Default.FitzScheduledRequestEnvelope);

    sealed class ScriptedNoticeClient(IReadOnlyList<ReadOnlyMemory<byte>> bodies) : INoticeClient
    {
        public Task PublishAsync(string route, ReadOnlyMemory<byte> body, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<NoticeSubscription> SubscribeAsync(string selector, CancellationToken ct = default) =>
            Task.FromResult(new NoticeSubscription(selector,
                Messages(selector, ct), _ => ValueTask.CompletedTask, Task.CompletedTask));

        async IAsyncEnumerable<NoticeMessage> Messages(string route, [EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var body in bodies)
            {
                ct.ThrowIfCancellationRequested();
                yield return new NoticeMessage(route, body);
                await Task.Yield();
            }
        }
    }

    sealed class ScriptedScheduleClient(IReadOnlyList<ReadOnlyMemory<byte>> payloads) : IScheduleClient
    {
        public Task<ScheduleSubscription> SubscribeAsync(string selector, CancellationToken ct = default) =>
            Task.FromResult(new ScheduleSubscription(selector,
                Notifications(selector, ct), _ => ValueTask.CompletedTask, Task.CompletedTask));

        public Task<string?> CreateAsync(string route, string cron, ScheduleDeliveryMode mode,
            ReadOnlyMemory<byte> payload, CancellationToken ct = default) => throw new NotSupportedException();

        public Task CancelAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<ScheduleListPage> ListAsync(ulong? offset, ulong? limit, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ScheduleEntry>> ListBySelectorAsync(string selector,
            CancellationToken ct = default) => throw new NotSupportedException();

        async IAsyncEnumerable<ScheduleNotification> Notifications(string route,
            [EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var payload in payloads)
            {
                ct.ThrowIfCancellationRequested();
                yield return new ScheduleNotification(route, payload);
                await Task.Yield();
            }
        }
    }
}
