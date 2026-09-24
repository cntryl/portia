using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Cntryl.Portia;

/// <summary>Declares shared Fitz capabilities and worker-only listeners.</summary>
public sealed class PortiaFitzBuilder
{
    static readonly RequestTransportId[] AllBuiltInTransports =
        [RequestTransportId.Callable, RequestTransportId.Queue, RequestTransportId.Notice, RequestTransportId.Schedule];

    readonly PortiaBuilder _application;
    readonly HashSet<string> _capabilities = new(StringComparer.Ordinal);
    readonly HashSet<RequestTransportId> _requiredWorkerTransports = [];
    readonly HashSet<RequestTransportId> _workerTransports = [.. AllBuiltInTransports];
    readonly Lazy<IReadOnlyList<FitzWorkerDefinition>> _workers;
    string? _checkpointRoute;
    bool _workerSelectionExplicit;

    internal PortiaFitzBuilder(PortiaBuilder application)
    {
        _application = application;
        _workers = new Lazy<IReadOnlyList<FitzWorkerDefinition>>(() => [.. BuildWorkers()],
            LazyThreadSafetyMode.ExecutionAndPublication);
        _ = application.ConfigureWorker("Portia.Fitz", services =>
        {
            _ = services.AddSingleton(this);
            _ = services.AddSingleton<IHostedService, FitzApplicationWorkers>();
        });
    }

    internal IReadOnlyList<FitzWorkerDefinition> Workers => _workers.Value;
    internal FleetRunOptions? Fleet { get; private set; }

    /// <summary>Registers event persistence, its reader/writer aliases, and JSON event serialization.</summary>
    internal PortiaFitzBuilder AddEventStore()
    {
        if (!_capabilities.Add("events"))
            return this;
        var services = _application.Services;
        services.TryAddSingleton<IEventStore>(provider => new FitzEventStore(
            provider.GetRequiredService<FitzApplicationConnection>().Client.Stream,
            provider.GetRequiredService<IDomainEventSerializer>()));
        services.TryAddSingleton<IDomainEventReader>(provider => provider.GetRequiredService<IEventStore>());
        services.TryAddSingleton<IDomainEventWriter>(provider => provider.GetRequiredService<IEventStore>());
        services.TryAddSingleton(provider =>
            (provider.GetRequiredService<IEventStore>() as IDomainEventNotifier)!);
        return this;
    }

    /// <summary>
    ///     Publishes the shared connection's KV client, so an application repository that derives from
    ///     <see cref="FitzKvProjectionStore" /> takes <see cref="IKvClient" /> as an ordinary constructor
    ///     dependency instead of owning a second connection to the same broker.
    /// </summary>
    internal PortiaFitzBuilder AddKvClient()
    {
        if (!_capabilities.Add("kv"))
            return this;
        _application.Services.TryAddSingleton<IKvClient>(provider =>
            provider.GetRequiredService<FitzApplicationConnection>().Client.Kv);
        return this;
    }

    /// <summary>Registers outbound RPC, queue, notice, and scheduling clients without starting listeners.</summary>
    internal PortiaFitzBuilder AddRequestClients()
    {
        if (!_capabilities.Add("clients"))
            return this;
        var services = _application.Services;
        AddSerializers(services);
        services.TryAddSingleton<IScheduleClient>(provider =>
            provider.GetRequiredService<FitzApplicationConnection>().Client.Schedule);
        services.TryAddSingleton<IRemoteRequestSender>(provider => new FitzRemoteRequestSender(
            provider.GetRequiredService<FitzApplicationConnection>().Client.Rpc,
            provider.GetRequiredService<IRequestSerializer>(),
            provider.GetRequiredService<IRequestOutcomeDeserializer>(),
            provider.GetRequiredService<RequestTransportCatalog>()));
        services.TryAddSingleton<IRequestQueuePublisher>(provider => new FitzRequestQueuePublisher(
            provider.GetRequiredService<FitzApplicationConnection>().Client.Queue,
            provider.GetRequiredService<IRequestSerializer>(),
            provider.GetRequiredService<RequestTransportCatalog>()));
        services.TryAddSingleton<INoticeRequestSender>(provider => new FitzNoticeRequestSender(
            provider.GetRequiredService<FitzApplicationConnection>().Client.Notice,
            provider.GetRequiredService<IRequestSerializer>(),
            provider.GetRequiredService<RequestTransportCatalog>()));
        services.TryAddSingleton<IRequestScheduler>(provider => new FitzRequestScheduler(
            provider.GetRequiredService<IScheduleClient>(),
            provider.GetRequiredService<IRequestSerializer>(),
            provider.GetRequiredService<RequestTransportCatalog>()));
        return this;
    }

    /// <summary>Activates every transport declared by requests with selected handlers.</summary>
    /// <returns>This builder, for chaining.</returns>
    public PortiaFitzBuilder AddRequestWorkers() => EnableWorkers(AllBuiltInTransports, false);

    /// <summary>Activates RPC serving when at least one selected handler accepts a callable request.</summary>
    /// <returns>This builder, for chaining.</returns>
    public PortiaFitzBuilder AddRpcWorkers() => EnableWorkers([RequestTransportId.Callable], true);

    /// <summary>Activates queue listeners for all selected handlers accepting queuable requests.</summary>
    /// <returns>This builder, for chaining.</returns>
    public PortiaFitzBuilder AddQueueWorkers() => EnableWorkers([RequestTransportId.Queue], true);

    /// <summary>Activates notice listeners for all selected handlers accepting notifiable requests.</summary>
    /// <returns>This builder, for chaining.</returns>
    public PortiaFitzBuilder AddNoticeWorkers() => EnableWorkers([RequestTransportId.Notice], true);

    /// <summary>Activates schedule listeners for all selected handlers accepting schedulable requests.</summary>
    /// <returns>This builder, for chaining.</returns>
    public PortiaFitzBuilder AddScheduledWorkers() => EnableWorkers([RequestTransportId.Schedule], true);

    /// <summary>Disables inbound request listeners while retaining Fitz persistence and outbound clients.</summary>
    /// <returns>This builder, for chaining.</returns>
    public PortiaFitzBuilder DisableRequestWorkers()
    {
        _workerTransports.Clear();
        _requiredWorkerTransports.Clear();
        _workerSelectionExplicit = true;
        return this;
    }

    /// <summary>Configures membership for the application's explicitly leased component workloads.</summary>
    /// <param name="options">The membership selector and its lease and timing settings.</param>
    /// <returns>This builder, for chaining.</returns>
    /// <exception cref="InvalidOperationException">The application already configured a different fleet.</exception>
    public PortiaFitzBuilder UseFleet(FleetRunOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate([]);
        if (Fleet is not null && Fleet != options)
            throw new InvalidOperationException("This application has conflicting Fitz fleet configurations.");
        Fleet = options;
        return this;
    }

    /// <summary>
    ///     Persists reactor progress in Fitz KV instead of requiring a second persistence technology.
    ///     This route is a base, not the literal resource every reactor transacts against:
    ///     <see cref="FitzKvCheckpointStore" /> derives one resource per reactor per tenant from it,
    ///     because this one store instance serves every reactor workload the app runs. Projector
    ///     progress is not registered here — a projector commits its checkpoint inside its own
    ///     repository's transaction, which is what <see cref="FitzKvProjectionStore" /> exists to
    ///     share, and that store derives its own per-workload resources the same way.
    /// </summary>
    /// <param name="route">The <c>kv://{realm}/{area}/{resource}</c> base route checkpoints are written under.</param>
    /// <returns>This builder, for chaining.</returns>
    /// <exception cref="ArgumentException">The route is not an exact three-segment Fitz KV route.</exception>
    /// <exception cref="InvalidOperationException">The application already selected a different route.</exception>
    public PortiaFitzBuilder UseKvCheckpoints(string route)
    {
        var validated = FitzKvCheckpoints.Route(route, nameof(route));
        if (_checkpointRoute is not null)
        {
            return string.Equals(_checkpointRoute, validated, StringComparison.Ordinal)
                ? this
                : throw new InvalidOperationException(
                    $"This application has conflicting Fitz KV checkpoint routes ('{_checkpointRoute}' and "
                    + $"'{validated}'). Reactor progress must resolve to one route, or a restart reads its "
                    + "checkpoints from a route that never received them and every reaction replays from zero.");
        }

        _checkpointRoute = validated;
        _ = AddKvClient();
        _application.Services.TryAddSingleton<IProjectionCheckpointStore>(provider =>
            new FitzKvCheckpointStore(provider.GetRequiredService<IKvClient>(), validated));
        return this;
    }

    PortiaFitzBuilder EnableWorkers(IEnumerable<RequestTransportId> transports, bool requireMatch)
    {
        if (_workerSelectionExplicit)
        {
            _workerTransports.UnionWith(transports);
        }
        else
        {
            _workerTransports.Clear();
            _workerTransports.UnionWith(transports);
            _workerSelectionExplicit = true;
        }

        if (requireMatch)
            _requiredWorkerTransports.UnionWith(transports);

        AddSerializers(_application.Services);
        return this;
    }

    IEnumerable<RequestTransportRegistration> SelectedRequests(RequestTransportId transport)
        => _application.SelectedRequests(transport);

    List<FitzWorkerDefinition> BuildWorkers()
    {
        ValidateRequiredWorkers();
        var workers = new List<FitzWorkerDefinition>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (_workerTransports.Contains(RequestTransportId.Callable) &&
            SelectedRequests(RequestTransportId.Callable).Any())
            AddWorker(new FitzRpcWorkerDefinition());
        // Each transport supplies the definition it wants built, so a new worker kind is a new
        // record and a line here rather than another arm in a switch over stringly-typed kinds.
        AddTransport(RequestTransportId.Queue, FitzQueueWorkerDefinition.For);
        AddTransport(RequestTransportId.Notice, FitzNoticeWorkerDefinition.For);
        AddTransport(RequestTransportId.Schedule, FitzScheduleWorkerDefinition.For);
        return workers;

        void AddTransport(RequestTransportId transport, Func<RequestRouteAttribute, FitzWorkerDefinition> create)
        {
            if (!_workerTransports.Contains(transport))
                return;

            foreach (var registration in SelectedRequests(transport))
                AddWorker(create(registration.Route));
        }

        void AddWorker(FitzWorkerDefinition worker)
        {
            if (keys.Add(worker.Key))
                workers.Add(worker);
        }
    }

    void ValidateRequiredWorkers()
    {
        Require(RequestTransportId.Callable, "AddRpcWorkers()", "callable");
        Require(RequestTransportId.Queue, "AddQueueWorkers()", "queuable");
        Require(RequestTransportId.Notice, "AddNoticeWorkers()", "notifiable");
        Require(RequestTransportId.Schedule, "AddScheduledWorkers()", "schedulable");

        void Require(RequestTransportId transport, string selector, string capability)
        {
            if (_requiredWorkerTransports.Contains(transport) && !SelectedRequests(transport).Any())
            {
                throw new InvalidOperationException(
                    $"{selector} selected no {capability} request handlers. Select a matching handler or remove the worker selector.");
            }
        }
    }

    static void AddSerializers(IServiceCollection services)
    {
        services.TryAddSingleton(provider =>
            new RequestTransportCatalog(provider.GetServices<RequestTransportRegistration>()));
        services.TryAddSingleton(provider => new JsonRequestSerializer(
            provider.GetServices<RequestTransportRegistration>(),
            provider.GetRequiredKeyedService<JsonSerializerOptions>(PortiaServiceKeys.Json)));
        services.TryAddSingleton<IRequestSerializer>(provider => provider.GetRequiredService<JsonRequestSerializer>());
        services.TryAddSingleton<IRequestDeserializer>(provider =>
            provider.GetRequiredService<JsonRequestSerializer>());
        services.TryAddSingleton<IRequestOutcomeSerializer>(provider =>
            provider.GetRequiredService<JsonRequestSerializer>());
        services.TryAddSingleton<IRequestOutcomeDeserializer>(provider =>
            provider.GetRequiredService<JsonRequestSerializer>());
    }
}
