using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Cntryl.Portia;

/// <summary>Declares shared Fitz capabilities and worker-only listeners.</summary>
public sealed class PortiaFitzBuilder
{
    const RequestTransports AllRequestTransports = RequestTransports.Callable | RequestTransports.Queuable
                                                                              | RequestTransports.Notifiable |
                                                                              RequestTransports.Schedulable;

    readonly PortiaBuilder _application;
    readonly HashSet<string> _capabilities = new(StringComparer.Ordinal);
    readonly Lazy<IReadOnlyList<FitzWorkerDefinition>> _workers;
    RequestTransports _requiredWorkerTransports;
    bool _workerSelectionExplicit;
    RequestTransports _workerTransports = AllRequestTransports;

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

    /// <summary>Registers outbound RPC, queue, notice, and scheduling clients without starting listeners.</summary>
    internal PortiaFitzBuilder AddRequestClients()
    {
        if (!_capabilities.Add("clients"))
            return this;
        var services = _application.Services;
        AddSerializers(services);
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
            provider.GetRequiredService<FitzApplicationConnection>().Client.Schedule,
            provider.GetRequiredService<IRequestSerializer>(),
            provider.GetRequiredService<RequestTransportCatalog>()));
        return this;
    }

    /// <summary>Activates every transport declared by requests with selected handlers.</summary>
    /// <returns>This builder, for chaining.</returns>
    public PortiaFitzBuilder AddRequestWorkers() => EnableWorkers(AllRequestTransports, false);

    /// <summary>Activates RPC serving when at least one selected handler accepts a callable request.</summary>
    /// <returns>This builder, for chaining.</returns>
    public PortiaFitzBuilder AddRpcWorkers() => EnableWorkers(RequestTransports.Callable, true);

    /// <summary>Activates queue listeners for all selected handlers accepting queuable requests.</summary>
    /// <returns>This builder, for chaining.</returns>
    public PortiaFitzBuilder AddQueueWorkers() => EnableWorkers(RequestTransports.Queuable, true);

    /// <summary>Activates notice listeners for all selected handlers accepting notifiable requests.</summary>
    /// <returns>This builder, for chaining.</returns>
    public PortiaFitzBuilder AddNoticeWorkers() => EnableWorkers(RequestTransports.Notifiable, true);

    /// <summary>Activates schedule listeners for all selected handlers accepting schedulable requests.</summary>
    /// <returns>This builder, for chaining.</returns>
    public PortiaFitzBuilder AddScheduledWorkers() => EnableWorkers(RequestTransports.Schedulable, true);

    /// <summary>Disables inbound request listeners while retaining Fitz persistence and outbound clients.</summary>
    /// <returns>This builder, for chaining.</returns>
    public PortiaFitzBuilder DisableRequestWorkers()
    {
        _workerTransports = 0;
        _requiredWorkerTransports = 0;
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

    PortiaFitzBuilder EnableWorkers(RequestTransports transports, bool requireMatch)
    {
        if (_workerSelectionExplicit)
        {
            _workerTransports |= transports;
        }
        else
        {
            _workerTransports = transports;
            _workerSelectionExplicit = true;
        }

        if (requireMatch)
            _requiredWorkerTransports |= transports;

        AddSerializers(_application.Services);
        return this;
    }

    IEnumerable<RequestTransportRegistration> SelectedRequests(RequestTransports transport)
        => _application.SelectedRequests(transport);

    List<FitzWorkerDefinition> BuildWorkers()
    {
        ValidateRequiredWorkers();
        var workers = new List<FitzWorkerDefinition>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (_workerTransports.HasFlag(RequestTransports.Callable) && SelectedRequests(RequestTransports.Callable).Any())
            AddWorker(new FitzRpcWorkerDefinition());
        // Each transport supplies the definition it wants built, so a new worker kind is a new
        // record and a line here rather than another arm in a switch over stringly-typed kinds.
        AddTransport(RequestTransports.Queuable, FitzQueueWorkerDefinition.For);
        AddTransport(RequestTransports.Notifiable, FitzNoticeWorkerDefinition.For);
        AddTransport(RequestTransports.Schedulable, FitzScheduleWorkerDefinition.For);
        return workers;

        void AddTransport(RequestTransports transport, Func<RequestRouteAttribute, FitzWorkerDefinition> create)
        {
            if (!_workerTransports.HasFlag(transport))
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
        Require(RequestTransports.Callable, "AddRpcWorkers()", "callable");
        Require(RequestTransports.Queuable, "AddQueueWorkers()", "queuable");
        Require(RequestTransports.Notifiable, "AddNoticeWorkers()", "notifiable");
        Require(RequestTransports.Schedulable, "AddScheduledWorkers()", "schedulable");

        void Require(RequestTransports transport, string selector, string capability)
        {
            if (_requiredWorkerTransports.HasFlag(transport) && !SelectedRequests(transport).Any())
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
            provider.GetRequiredService<JsonSerializerOptions>()));
        services.TryAddSingleton<IRequestSerializer>(provider => provider.GetRequiredService<JsonRequestSerializer>());
        services.TryAddSingleton<IRequestDeserializer>(provider =>
            provider.GetRequiredService<JsonRequestSerializer>());
        services.TryAddSingleton<IRequestOutcomeSerializer>(provider =>
            provider.GetRequiredService<JsonRequestSerializer>());
        services.TryAddSingleton<IRequestOutcomeDeserializer>(provider =>
            provider.GetRequiredService<JsonRequestSerializer>());
    }
}
