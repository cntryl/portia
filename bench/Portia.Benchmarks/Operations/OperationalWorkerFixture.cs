using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cntryl.Fitz;
using Cntryl.Portia.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cntryl.Portia;

[Discriminator("benchmark.journal_event", 1)]
sealed record JournalBenchmarkEvent(int Sequence) : DomainEvent;

[PortiaJsonContext]
[JsonSerializable(typeof(JournalBenchmarkEvent))]
sealed partial class JournalBenchmarkJsonContext : JsonSerializerContext;

sealed class JournalBenchmarkOutage
{
    internal volatile bool Active;
}

sealed class JournalBenchmarkWorkload(string realm, int index, JournalBenchmarkOutage outage)
{
    readonly ConcurrentDictionary<int, long> _published = new();
    readonly ConcurrentQueue<double> _commitLatencies = new();
    readonly Uuid _aggregate = Uuid.CreateVersion4();
    EventCursor _cursor = EventCursor.Start;
    int _checkpointLoads;
    int _passes;
    int _batches;
    int _sequence;

    internal string Name { get; } = $"projector-{index}";
    internal EventStreamPattern Pattern { get; } = EventStreamPattern.ForPattern(realm, $"events-{index}");
    internal string ProjectionRoute { get; } = $"kv://{realm}/performance/projection";
    internal OperationalTarget Target => new(Name, Pattern.ToString(), OperationalScope.Global);
    internal CheckpointIdentity Identity => new(Name, Pattern);
    internal bool OutageActive => outage.Active;
    internal int PublishedCount => _sequence;
    internal int Passes => Volatile.Read(ref _passes);
    internal int CheckpointLoads => Volatile.Read(ref _checkpointLoads);
    internal int Batches => Volatile.Read(ref _batches);
    internal double[] CommitLatencies => _commitLatencies.ToArray();
    internal AsyncLocal<bool> IsOperatorProbe { get; } = new();

    internal async Task<EventCursor> PublishAsync(IEventStore events, int count, CancellationToken ct)
    {
        var stream = new EventStreamAddress(Pattern.Realm, Pattern.Area!, _aggregate.ToString());
        var batch = new DomainEvent[count];
        var startingRevision = (ulong)_sequence;
        var started = Stopwatch.GetTimestamp();

        for (var index = 0; index < count; index++)
        {
            var sequence = ++_sequence;
            batch[index] = DomainEventSeed.Attach(new JournalBenchmarkEvent(sequence), _aggregate, (ulong)sequence);
            _published[sequence] = started;
        }

        await events.AppendAsync(stream, startingRevision, batch, ct);

        await foreach (var record in events.ReadAsync(Pattern, _cursor, ct))
            _cursor = record.NextCursor;

        return _cursor;
    }

    internal void CountCheckpointLoad(bool firstForPass)
    {
        if (IsOperatorProbe.Value)
            return;

        Interlocked.Increment(ref _checkpointLoads);

        if (firstForPass)
            Interlocked.Increment(ref _passes);
    }

    internal void CountBatch() => Interlocked.Increment(ref _batches);

    internal void RecordCommit(int sequence) =>
        _commitLatencies.Enqueue(Stopwatch.GetElapsedTime(_published[sequence]).TotalMilliseconds);
}

sealed class JournalBenchmarkProjection(IKvClient kv, JournalBenchmarkWorkload workload) :
    FitzKvProjectionStore(kv, workload.ProjectionRoute, workload.Name, KvDurability.Sync), IProjectionStore
{
    int _sequence;
    bool _loadedCheckpoint;

    ValueTask<ProjectionCheckpoint> IProjectionStore.LoadCheckpointAsync(CheckpointIdentity identity, CancellationToken ct)
    {
        workload.CountCheckpointLoad(!_loadedCheckpoint);
        _loadedCheckpoint = true;
        return LoadCheckpointAsync(identity, ct);
    }

    async ValueTask<IProjectionBatch> IProjectionStore.BeginAsync(ProjectionBatchContext context, CancellationToken ct)
    {
        workload.CountBatch();
        return new MeasuredBatch(await BeginAsync(context, ct), this);
    }

    internal async ValueTask StoreAsync(int sequence, CancellationToken ct)
    {
        _sequence = sequence;
        await Transaction.PutAsync(EventKey(sequence), "applied"u8.ToArray(), ct);
    }

    internal async ValueTask<bool> HasAppliedAsync(int sequence, CancellationToken ct)
    {
        await using var tx = await BeginReadAsync(workload.Pattern.Realm, ct);
        return await tx.GetAsync(EventKey(sequence), ct) is KvFound;
    }

    static byte[] EventKey(int sequence) => Encoding.UTF8.GetBytes(sequence.ToString(CultureInfo.InvariantCulture));

    sealed class MeasuredBatch(IProjectionBatch batch, JournalBenchmarkProjection projection) : IProjectionBatch
    {
        public async ValueTask CommitAsync(ProjectionCheckpoint checkpoint, CancellationToken ct = default)
        {
            await batch.CommitAsync(checkpoint, ct);
            projection.RecordCommit();
        }

        public ValueTask DisposeAsync() => batch.DisposeAsync();
    }

    void RecordCommit() => workload.RecordCommit(_sequence);
}

sealed class JournalBenchmarkProjector(JournalBenchmarkProjection store, JournalBenchmarkWorkload workload)
    : Projector(store, workload.Pattern)
{
    protected override async ValueTask<Result> ProjectEventAsync(
        DomainEventRecord record,
        IProjectorContext context,
        CancellationToken ct
    )
    {
        if (workload.OutageActive)
            return Result.Failure(RequestError.Validation("shared application dependency closed", isTransient: true));

        await store.StoreAsync(((JournalBenchmarkEvent)record.Event).Sequence, ct);
        return Result.Success;
    }
}

sealed class JournalBenchmarkStore(FitzOperationalStore store, JournalBenchmarkWorkload workload) : IOperationalStore
{
    int _reads;
    int _writes;
    int _rejectedWrites;
    readonly ConcurrentQueue<OperationalProcessorState> _written = new();
    long _stateBytes;

    internal int Reads => Volatile.Read(ref _reads);
    internal int Writes => Volatile.Read(ref _writes);
    internal int RejectedWrites => Volatile.Read(ref _rejectedWrites);
    internal long MeasureStateBytes()
    {
        while (_written.TryDequeue(out var state))
            _stateBytes += SerializedBytes(state);

        return _stateBytes;
    }

    public ValueTask<OperationalProcessorState?> ReadAsync(OperationalTarget target, CancellationToken ct = default)
    {
        if (!workload.IsOperatorProbe.Value)
            Interlocked.Increment(ref _reads);

        return store.ReadAsync(target, ct);
    }

    public ValueTask<IReadOnlyList<OperationalProcessorState>> ListAsync(OperationalScope scope, CancellationToken ct = default) => store.ListAsync(scope, ct);

    public async ValueTask<bool> CompareExchangeAsync(
        OperationalProcessorState replacement,
        long expectedRevision,
        Guid expectedOwner,
        CancellationToken ct = default
    )
    {
        if (workload.IsOperatorProbe.Value)
            return await store.CompareExchangeAsync(replacement, expectedRevision, expectedOwner, ct);

        Interlocked.Increment(ref _writes);
        var changed = await store.CompareExchangeAsync(replacement, expectedRevision, expectedOwner, ct);

        if (changed)
            _written.Enqueue(replacement);
        else
            Interlocked.Increment(ref _rejectedWrites);

        return changed;
    }

    internal static int SerializedBytes(OperationalProcessorState state) =>
        JsonSerializer.SerializeToUtf8Bytes(state, FitzJsonContext.Default.OperationalProcessorState).Length;
}

sealed class JournalBenchmarkHost : IAsyncDisposable
{
    const string AdministratorRole = "journal-benchmark-operator";
    static readonly ClaimsPrincipal Reader = Actor("diagnostician");
    static readonly ClaimsPrincipal Approver = Actor("approver");
    readonly IHost _host;
    IServiceProvider Services => _host.Services;

    internal JournalBenchmarkHost(FitzClient client, JournalBenchmarkWorkload workload, string route)
    {
        Workload = workload;
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        var services = builder.Services;
        services.AddSingleton(workload);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["PORTIA_SYSTEM_ADMIN_ROLE"] = AdministratorRole }).Build());
        services.AddSingleton<IOperationalAuthorizer, LocalAuthorization>();
        services.AddSingleton<IEventStore>(new FitzEventStore(client.Stream, new JsonDomainEventSerializer(
            new DomainEventTypeCatalog().Register<JournalBenchmarkEvent>(1, "benchmark.journal_event"),
            null, JournalBenchmarkJsonContext.Default.Options)));
        services.AddScoped<JournalBenchmarkProjection>();
        services.AddPortia().UseFitzClient(client, fitz => fitz.DisableRequestWorkers()
                .UseFleet(new FleetRunOptions
                {
                    MembershipSelector = $"lease://{workload.Pattern.Realm}/members-{workload.Name}/*"
                }).UseOperations(route))
            .AddProjector<JournalBenchmarkProjector>(workload.Name, WorkloadScope.Global, options =>
            {
                options.PollInterval = TimeSpan.FromMilliseconds(25);
                options.MaximumFailureDelay = TimeSpan.FromMilliseconds(200);
                options.FailureAttemptLimit = 4;
                options.Processing = new ProjectionRunOptions { MaxBatchSize = 1, MaxEventsPerPass = 1 };
            })
            .AddWorkers();
        services.AddSingleton<JournalBenchmarkStore>();
        services.AddSingleton<IOperationalStore>(provider => provider.GetRequiredService<JournalBenchmarkStore>());
        _host = builder.Build();
        RawStore = Services.GetRequiredService<FitzOperationalStore>();
        MeasuredStore = Services.GetRequiredService<JournalBenchmarkStore>();
        Projection = new JournalBenchmarkProjection(client.Kv, workload);
    }

    internal JournalBenchmarkWorkload Workload { get; }
    internal FitzOperationalStore RawStore { get; }
    internal JournalBenchmarkStore MeasuredStore { get; }
    internal JournalBenchmarkProjection Projection { get; }
    internal IEventStore Events => Services.GetRequiredService<IEventStore>();
    internal Task StartAsync(CancellationToken ct) => _host.StartAsync(ct);
    internal IPortiaOperations Operations => Services.GetRequiredService<IPortiaOperations>();

    internal async Task<double> ReadStatusAsync(CancellationToken ct)
    {
        Workload.IsOperatorProbe.Value = true;

        try
        {
            var started = Stopwatch.GetTimestamp();
            var status = await Services.GetRequiredService<IRequestBus>()
                .SendAsync(new GetProcessorStatus(Workload.Name), Reader, ct);

            if (!status.IsSuccess)
                throw new InvalidOperationException($"Status query failed for {Workload.Name}: {status.Error}.");

            if (status.Value.Progress is null || status.Value.FailureError is not null)
                throw new InvalidOperationException($"Status query returned incomplete evidence for {Workload.Name}.");

            return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        finally
        {
            Workload.IsOperatorProbe.Value = false;
        }
    }

    internal async Task<RepairPlan> QueueExactRepairAsync(OperationalAttempt failed, CancellationToken ct)
    {
        Workload.IsOperatorProbe.Value = true;

        try
        {
            var plan = await Operations.PreviewRepairAsync(Reader, Workload.Target, ct);

            if (plan.FailedAttemptId != failed.Id || plan.ExpectedCheckpoint != failed.ExpectedCheckpoint
                                                  || !plan.Sources.SequenceEqual(failed.Sources))
                throw new InvalidOperationException("Repair did not bind the exact failed attempt and source.");

            _ = await Operations.ApproveRepairAsync(Approver, Workload.Target, plan.Id, ct);
            _ = await Operations.ExecuteRepairAsync(Reader, Workload.Target, plan.Id, ct);
            return plan;
        }
        finally
        {
            Workload.IsOperatorProbe.Value = false;
        }
    }

    static ClaimsPrincipal Actor(string name) => new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, name),
            new Claim(ClaimTypes.Role, AdministratorRole)
        ], "benchmark"));

    public async ValueTask DisposeAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        try
        {
            await _host.StopAsync(deadline.Token);
        }
        finally
        {
            if (_host is IAsyncDisposable asyncHost)
                await asyncHost.DisposeAsync();
            else
                _host.Dispose();
        }
    }

    sealed class LocalAuthorization : IOperationalAuthorizer
    {
        public ValueTask<bool> AuthorizeAsync(
            ClaimsPrincipal actor,
            OperationalScope scope,
            OperationalAccess permission,
            CancellationToken ct = default
        )
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(scope == OperationalScope.Global && actor.IsInRole(AdministratorRole)
                && (permission != OperationalAccess.Approve || actor.FindFirstValue(ClaimTypes.NameIdentifier) == "approver"));
        }
    }
}
