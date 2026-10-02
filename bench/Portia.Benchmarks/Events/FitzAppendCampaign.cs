using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cntryl.Fitz;

namespace Cntryl.Portia;

static class FitzAppendCampaign
{
    static readonly int[] Counts = [1, 8, 128];
    static readonly int[] PayloadSizes = [256, 4096];

    internal static async Task RunAsync(string output, Uri endpoint, int repetitions = 30)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(repetitions, 20);
        Directory.CreateDirectory(output);
        var wire = new FitzWireCounts();
        await using var client = CreateClient(endpoint, wire);
        await client.ConnectWhenReadyAsync(new ConnectWhenReadyOptions(TimeSpan.FromSeconds(20)));
        var serializer = Serializer();
        var store = new FitzEventStore(client.Stream, serializer);
        var rows = new List<FitzAppendSample>();
        var readbacks = new List<FitzReadback>();
        foreach (var count in Counts)
            foreach (var size in PayloadSizes)
            {
                var payloadOverhead = JsonSerializer.SerializeToUtf8Bytes(new FitzCampaignEvent(string.Empty),
                    FitzCampaignJsonContext.Default.FitzCampaignEvent).Length;
                var payload = new string('a', size - payloadOverhead);
                if (JsonSerializer.SerializeToUtf8Bytes(new FitzCampaignEvent(payload),
                        FitzCampaignJsonContext.Default.FitzCampaignEvent).Length != size)
                    throw new InvalidOperationException("Prepared serialized business payload differs from its declared byte size.");
                // Each cohort owns one persistent aggregate stream. Payload setup is outside append timing.
                var aggregate = Uuid.CreateVersion4();
                var stream = new EventStreamAddress("performance", "append", aggregate.ToString());
                var expectedHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using (expectedHash)
                {
                    ulong position = 0;
                    for (var invocation = 0; invocation < repetitions * 10 + 10; invocation++)
                    {
                        var events = CreateEvents(aggregate, position, count, payload);
                        foreach (var ev in events)
                            expectedHash.AppendData(serializer.Serialize(ev).Span);
                        var before = wire.Snapshot();
                        var allocated = GC.GetTotalAllocatedBytes(true);
                        var started = Stopwatch.GetTimestamp();
                        await store.AppendAsync(stream, position, events);
                        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                        var allocation = GC.GetTotalAllocatedBytes(true) - allocated;
                        var actual = wire.Snapshot() - before;
                        if (actual.Begin != 1 || actual.Append != count || actual.Commit != 1 || actual.Rollback != 0)
                            throw new InvalidOperationException("Observed Fitz append wire counts differ from the atomic adapter contract.");
                        if (invocation >= 10)
                            rows.Add(new FitzAppendSample(count, size, serializer.Serialize(events[0]).Length,
                                (invocation - 10) / 10, (invocation - 10) % 10, elapsed, allocation,
                                actual.Begin, actual.Append, actual.Commit, actual.Rollback));
                        position += (ulong)count;
                    }
                    var expected = new FitzReadback(stream.ToString(), position, count, size,
                        Convert.ToHexString(expectedHash.GetHashAndReset()));
                    await VerifyAsync(store, serializer, expected);
                    readbacks.Add(expected);
                }
                await File.WriteAllBytesAsync(Path.Combine(output, "raw.json"),
                    JsonSerializer.SerializeToUtf8Bytes(rows, FitzCampaignJsonContext.Default.ListFitzAppendSample));
                Console.WriteLine($"Fitz {count} events × {size} payload bytes: {repetitions * 10} measured atomic appends; wire and content verified.");
            }
        await File.WriteAllBytesAsync(Path.Combine(output, "readback-manifest.json"),
            JsonSerializer.SerializeToUtf8Bytes(readbacks, FitzCampaignJsonContext.Default.ListFitzReadback));
        await File.WriteAllBytesAsync(Path.Combine(output, "environment.json"),
            JsonSerializer.SerializeToUtf8Bytes(new FitzCampaignEnvironment(RuntimeInformation.FrameworkDescription,
                RuntimeInformation.OSDescription, RuntimeInformation.ProcessArchitecture.ToString(), Environment.ProcessorCount,
                typeof(Client).Assembly.GetName().Version!.ToString(), endpoint.ToString(), repetitions, 10,
                "10 excluded warmup appends; 10 operations/repetition; single persistent stream/cohort; payload/event creation excluded; PayloadBytes is exact source-generated business JSON byte length; actual durable envelope size reported separately; process-total allocations include client receive workers; single-node local broker durability; no exporter waiting"),
                FitzCampaignJsonContext.Default.FitzCampaignEnvironment));
    }

    internal static async Task ReadbackAsync(string output, Uri endpoint)
    {
        var expected = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(Path.Combine(output, "readback-manifest.json")),
            FitzCampaignJsonContext.Default.ListFitzReadback)!;
        await using var client = CreateClient(endpoint, new FitzWireCounts());
        await client.ConnectWhenReadyAsync(new ConnectWhenReadyOptions(TimeSpan.FromSeconds(20)));
        var serializer = Serializer();
        var store = new FitzEventStore(client.Stream, serializer);
        foreach (var entry in expected)
            await VerifyAsync(store, serializer, entry);
        await File.WriteAllTextAsync(Path.Combine(output, "restart-readback.txt"),
            $"Verified {expected.Count} persistent streams after broker restart: ordered offsets, versions, payloads and exact envelope hashes.\n");
    }

    static Client CreateClient(Uri endpoint, FitzWireCounts counts) => new(new ClientConfig(endpoint,
        Timeout: TimeSpan.FromSeconds(20), TransportFactory: configuration => new FitzCountingTransport(
            new WebSocketTransport(configuration.Url, configuration.Timeout ?? TimeSpan.FromSeconds(20),
                configuration.MaxFrameSize, configuration.WebSocket, configuration.Heartbeat), counts)));

    static JsonDomainEventSerializer Serializer() => new(
        new DomainEventTypeCatalog().Register<FitzCampaignEvent>(1, "benchmark.fitz.append"), null,
        FitzCampaignJsonContext.Default.Options);

    static DomainEvent[] CreateEvents(Uuid aggregate, ulong position, int count, string payload) =>
        Enumerable.Range(0, count).Select(index =>
        {
            var ev = new FitzCampaignEvent(payload);
            ev.AttachMetadata(new DomainEventMetadata(Uuid.CreateVersion4(), aggregate, position + (ulong)index + 1,
                DateTimeOffset.UnixEpoch));
            return (DomainEvent)ev;
        }).ToArray();

    static async Task VerifyAsync(FitzEventStore store, JsonDomainEventSerializer serializer, FitzReadback expected)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        ulong observed = 0;
        await foreach (var record in store.ReadAsync(EventStreamAddress.Parse(expected.Stream), 0, CancellationToken.None))
        {
            var ev = (FitzCampaignEvent)record.Event;
            if (record.ResourceOffset != observed || ev.Metadata.AggregateVersion != observed + 1 ||
                JsonSerializer.SerializeToUtf8Bytes(ev, FitzCampaignJsonContext.Default.FitzCampaignEvent).Length != expected.PayloadBytes ||
                ev.Payload.Any(value => value != 'a'))
                throw new InvalidOperationException("Fitz committed readback violated content or ordered offsets.");
            hash.AppendData(serializer.Serialize(ev).Span);
            observed++;
        }
        if (observed != expected.Events || Convert.ToHexString(hash.GetHashAndReset()) != expected.Sha256)
            throw new InvalidOperationException("Fitz committed record count or envelope hash differs from the append input.");
    }
}

sealed class FitzCountingTransport(ITransport inner, FitzWireCounts counts) : ITransport
{
    public string TransportName => "websocket-counted";
    public Uri Url => inner.Url;
    public Task ConnectAsync(CancellationToken ct = default) => inner.ConnectAsync(ct);
    public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        counts.Observe(data.Span);
        return inner.SendAsync(data, ct);
    }
    public ValueTask<PooledFrame> ReceiveAsync(CancellationToken ct = default) => inner.ReceiveAsync(ct);
    public Task CloseAsync(CancellationToken ct = default) => inner.CloseAsync(ct);
    public ValueTask DisposeAsync() => inner.DisposeAsync();
}

sealed class FitzWireCounts
{
    long _begin;
    long _append;
    long _commit;
    long _rollback;
    internal FitzWireSnapshot Snapshot() => new(Interlocked.Read(ref _begin), Interlocked.Read(ref _append),
        Interlocked.Read(ref _commit), Interlocked.Read(ref _rollback));
    internal void Observe(ReadOnlySpan<byte> bytes)
    {
        // Verified against the pinned 1.4.2 FrameCodec: a CORRELATE label and a request share a frame.
        while (!bytes.IsEmpty)
        {
            var extended = bytes[0] == 0xFF;
            var header = extended ? 5 : 3;
            if (bytes.Length < header)
                throw new InvalidOperationException("Truncated counted Fitz frame.");
            var opcode = extended ? BinaryPrimitives.ReadUInt16BigEndian(bytes[1..]) : bytes[0];
            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes[(extended ? 3 : 1)..]);
            if (bytes.Length < header + length)
                throw new InvalidOperationException("Truncated counted Fitz payload.");
            switch (opcode)
            {
                case 600:
                    Interlocked.Increment(ref _begin);
                    break;
                case 601:
                    Interlocked.Increment(ref _append);
                    break;
                case 602:
                    Interlocked.Increment(ref _commit);
                    break;
                case 603:
                    Interlocked.Increment(ref _rollback);
                    break;
            }
            bytes = bytes[(header + length)..];
        }
    }
}

readonly record struct FitzWireSnapshot(long Begin, long Append, long Commit, long Rollback)
{
    public static FitzWireSnapshot operator -(FitzWireSnapshot left, FitzWireSnapshot right) =>
        new(left.Begin - right.Begin, left.Append - right.Append, left.Commit - right.Commit, left.Rollback - right.Rollback);
}

sealed record FitzAppendSample(int Events, int PayloadBytes, int EncodedEnvelopeBytes, int Repetition, int Invocation,
    double Milliseconds, long AllocatedBytes, long BeginFrames, long AppendFrames, long CommitFrames, long RollbackFrames);
sealed record FitzReadback(string Stream, ulong Events, int AppendSize, int PayloadBytes, string Sha256);
sealed record FitzCampaignEnvironment(string Framework, string Os, string Architecture, int Processors, string ClientVersion,
    string Endpoint, int Repetitions, int OperationsPerRepetition, string Boundary);
[Discriminator("benchmark.fitz.append")]
sealed record FitzCampaignEvent(string Payload) : DomainEvent;

[PortiaJsonContext]
[JsonSerializable(typeof(FitzCampaignEvent))]
[JsonSerializable(typeof(List<FitzAppendSample>))]
[JsonSerializable(typeof(List<FitzReadback>))]
[JsonSerializable(typeof(FitzCampaignEnvironment))]
sealed partial class FitzCampaignJsonContext : JsonSerializerContext;
