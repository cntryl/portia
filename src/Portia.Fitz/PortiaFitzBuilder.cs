using Cntryl.Fitz;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Cntryl.Portia;

/// <summary>Declares shared Fitz capabilities and worker-only listeners.</summary>
public sealed class PortiaFitzBuilder
{
    const RequestTransports AllRequestTransports = RequestTransports.Callable | RequestTransports.Queuable
        | RequestTransports.Notifiable | RequestTransports.Schedulable;
    readonly PortiaBuilder _application;
    readonly HashSet<string> _capabilities = new(StringComparer.Ordinal);
    readonly Lazy<IReadOnlyList<FitzWorkerDefinition>> _workers;
    RequestTransports _workerTransports = AllRequestTransports;
    RequestTransports _requiredWorkerTransports;
    bool _workerSelectionExplicit;

    internal PortiaFitzBuilder(PortiaBuilder application)
    {
        _application = application;
        _workers = new(() => [.. BuildWorkers()], LazyThreadSafetyMode.ExecutionAndPublication);
        _ = application.ConfigureWorker("Portia.Fitz", services => _ = services.AddSingleton<IHostedService>(provider => new FitzApplicationWorkers(provider, this)));
    }

    internal IReadOnlyList<FitzWorkerDefinition> Workers => _workers.Value;
    internal FleetRunOptions? Fleet { get; private set; }

    /// <summary>Registers event persistence, its reader/writer aliases, and JSON event serialization.</summary>
    internal PortiaFitzBuilder AddEventStore()
    {
        if (!_capabilities.Add("events"))
            return this;
        var services = _application.Services;
        services.TryAddSingleton<IDomainEventSerializer, JsonDomainEventSerializer>();
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
            provider.GetRequiredService<IRequestSerializer>(), provider.GetRequiredService<IRequestOutcomeDeserializer>()));
        services.TryAddSingleton<IRequestQueuePublisher>(provider => new FitzRequestQueuePublisher(
            provider.GetRequiredService<FitzApplicationConnection>().Client.Queue, provider.GetRequiredService<IRequestSerializer>()));
        services.TryAddSingleton<INoticeRequestSender>(provider => new FitzNoticeRequestSender(
            provider.GetRequiredService<FitzApplicationConnection>().Client.Notice, provider.GetRequiredService<IRequestSerializer>()));
        services.TryAddSingleton<IRequestScheduler>(provider => new FitzRequestScheduler(
            provider.GetRequiredService<FitzApplicationConnection>().Client.Schedule, provider.GetRequiredService<IRequestSerializer>()));
        return this;
    }

    /// <summary>Activates every transport declared by requests with selected handlers.</summary>
    public PortiaFitzBuilder AddRequestWorkers() => EnableWorkers(AllRequestTransports, requireMatch: false);

    /// <summary>Activates RPC serving when at least one selected handler accepts a callable request.</summary>
    public PortiaFitzBuilder AddRpcWorkers() => EnableWorkers(RequestTransports.Callable, requireMatch: true);

    /// <summary>Activates queue listeners for all selected handlers accepting queuable requests.</summary>
    public PortiaFitzBuilder AddQueueWorkers() => EnableWorkers(RequestTransports.Queuable, requireMatch: true);

    /// <summary>Activates notice listeners for all selected handlers accepting notifiable requests.</summary>
    public PortiaFitzBuilder AddNoticeWorkers() => EnableWorkers(RequestTransports.Notifiable, requireMatch: true);

    /// <summary>Activates schedule listeners for all selected handlers accepting schedulable requests.</summary>
    public PortiaFitzBuilder AddScheduledWorkers() => EnableWorkers(RequestTransports.Schedulable, requireMatch: true);

    /// <summary>Disables inbound request listeners while retaining Fitz persistence and outbound clients.</summary>
    public PortiaFitzBuilder DisableRequestWorkers()
    {
        _workerTransports = 0;
        _requiredWorkerTransports = 0;
        _workerSelectionExplicit = true;
        return this;
    }

    /// <summary>Configures membership for the application's explicitly leased component workloads.</summary>
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
    {
        var handled = _application.Services
            .Select(service => service.ImplementationInstance)
            .OfType<RequestHandlerRegistration>()
            .Select(registration => registration.RequestType)
            .ToHashSet();
        return _application.Services
            .Select(service => service.ImplementationInstance)
            .OfType<RequestTransportRegistration>()
            .Where(registration => handled.Contains(registration.RequestType) && registration.Transports.HasFlag(transport));
    }

    List<FitzWorkerDefinition> BuildWorkers()
    {
        ValidateRequiredWorkers();
        var workers = new List<FitzWorkerDefinition>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (_workerTransports.HasFlag(RequestTransports.Callable) && SelectedRequests(RequestTransports.Callable).Any())
            AddWorker("rpc", "");
        AddTransport(RequestTransports.Queuable, "queue", includeOperation: false);
        AddTransport(RequestTransports.Notifiable, "notice", includeOperation: false);
        AddTransport(RequestTransports.Schedulable, "schedule", includeOperation: true);
        return workers;

        void AddTransport(RequestTransports transport, string kind, bool includeOperation)
        {
            if (!_workerTransports.HasFlag(transport))
                return;
            foreach (var registration in SelectedRequests(transport))
            {
                var route = registration.Route;
                AddWorker(kind, includeOperation
                    ? $"{kind}://{route.Realm}/{route.Area}/{route.Resource}/{route.Operation}"
                    : $"{kind}://{route.Realm}/{route.Area}/{route.Resource}");
            }
        }

        void AddWorker(string kind, string route)
        {
            if (kind == "rpc")
            {
                if (keys.Add("rpc:"))
                    workers.Add(new FitzWorkerDefinition(kind, route));
                return;
            }
            ArgumentException.ThrowIfNullOrWhiteSpace(route);
            var segments = route.StartsWith(kind + "://", StringComparison.Ordinal) ? route[(kind.Length + 3)..].Split('/') : [];
            if (segments.Length != (kind == "schedule" ? 4 : 3) || segments.Any(string.IsNullOrWhiteSpace))
                throw new ArgumentException($"Invalid {kind} route '{route}'.", nameof(route));
            if (keys.Add(kind + ":" + route))
                workers.Add(new FitzWorkerDefinition(kind, route));
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
        services.TryAddSingleton<JsonRequestSerializer>();
        services.TryAddSingleton<IRequestSerializer>(provider => provider.GetRequiredService<JsonRequestSerializer>());
        services.TryAddSingleton<IRequestDeserializer>(provider => provider.GetRequiredService<JsonRequestSerializer>());
        services.TryAddSingleton<IRequestOutcomeSerializer>(provider => provider.GetRequiredService<JsonRequestSerializer>());
        services.TryAddSingleton<IRequestOutcomeDeserializer>(provider => provider.GetRequiredService<JsonRequestSerializer>());
    }
}

/// <summary>Connects shared Portia application setup to Fitz.</summary>
public static class PortiaFitzApplicationExtensions
{
    /// <summary>Adds Fitz persistence, clients, coordination, and workers for every selected request transport.</summary>
    public static PortiaBuilder AddFitz(this PortiaBuilder application, IConfiguration configuration) =>
        AddFitz(application, configuration, static _ => { });

    /// <summary>Configures an owned client from Endpoint and optional Token and StartupTimeoutSeconds settings.</summary>
    public static PortiaBuilder AddFitz(this PortiaBuilder application, IConfiguration configuration, Action<PortiaFitzBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var endpoint = configuration["Endpoint"];
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("ws" or "wss"))
            throw new ArgumentException("Fitz:Endpoint must be an absolute ws:// or wss:// URI.", nameof(configuration));
        var token = configuration["Token"];
        var timeout = TimeSpan.FromSeconds(15);
        if (configuration["StartupTimeoutSeconds"] is { } text)
        {
            if (!double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
                || !double.IsFinite(seconds) || seconds <= 0 || seconds > 3600)
            {
                throw new ArgumentException("Fitz:StartupTimeoutSeconds must be between zero and 3600 seconds.", nameof(configuration));
            }

            timeout = TimeSpan.FromSeconds(seconds);
        }
        var settings = new ClientConfig(uri, TokenProvider: token is null ? null : _ => ValueTask.FromResult(token));
        return Register(application, builder =>
        {
            if (configuration["ApplicationName"] is { } name)
            {
                if (!FleetRunOptions.IsSegment(name))
                    throw new ArgumentException("Fitz:ApplicationName must be an exact route segment.", nameof(configuration));
                _ = builder.UseFleet(new FleetRunOptions { MembershipSelector = $"lease://{name}/portia-members/*" });
            }
            configure(builder);
        }, _ => new FitzApplicationConnection(new Client(settings), true, timeout), (uri, token, timeout));
    }

    /// <summary>Configures an owned client, including an optional rotating backend token provider.</summary>
    public static PortiaBuilder AddFitz(
        this PortiaBuilder application,
        ClientConfig configuration,
        TimeSpan? startupTimeout = null) =>
        AddFitz(application, configuration, static _ => { }, startupTimeout);

    /// <summary>Configures an owned client with optional worker selection or fleet membership.</summary>
    public static PortiaBuilder AddFitz(this PortiaBuilder application, ClientConfig configuration,
        Action<PortiaFitzBuilder> configure, TimeSpan? startupTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var timeout = startupTimeout ?? TimeSpan.FromSeconds(15);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        return Register(application, configure, provider => new FitzApplicationConnection(new Client(configuration), true, timeout), (configuration, timeout));
    }

    /// <summary>Uses an already connected client and hosts every selected request transport.</summary>
    public static PortiaBuilder UseFitzClient(this PortiaBuilder application, Client client) =>
        UseFitzClient(application, client, static _ => { });

    /// <summary>Uses an already connected client with optional worker selection or fleet membership.</summary>
    public static PortiaBuilder UseFitzClient(this PortiaBuilder application, Client client, Action<PortiaFitzBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(client);
        return Register(application, configure, _ => new FitzApplicationConnection(client, false, TimeSpan.Zero), client);
    }

    static PortiaBuilder Register(PortiaBuilder application, Action<PortiaFitzBuilder> configure,
        Func<IServiceProvider, FitzApplicationConnection> connection, object identity)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(configure);
        var services = application.Services;
        var existing = services.Select(service => service.ImplementationInstance).OfType<FitzSetup>().SingleOrDefault();
        if (existing is not null)
        {
            if (!Equals(existing.Identity, identity))
                throw new InvalidOperationException("This application has conflicting Fitz connections. Configure one shared connection.");
            configure(existing.Builder);
            return application;
        }
        _ = services.AddSingleton(connection);
        _ = services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<FitzApplicationConnection>());
        var builder = new PortiaFitzBuilder(application);
        _ = services.AddSingleton(new FitzSetup(identity, builder));
        services.TryAddSingleton<IWorkloadCoordinator>(provider => new FitzWorkloadCoordinator(
            provider.GetRequiredService<FitzApplicationConnection>(), builder,
            provider.GetService<Microsoft.Extensions.Logging.ILogger<FleetPartitionRunner>>(),
            provider.GetService<TimeProvider>()));
        _ = builder.AddEventStore().AddRequestClients();
        configure(builder);
        return application;
    }

    sealed record FitzSetup(object Identity, PortiaFitzBuilder Builder);
}

sealed record FitzWorkerDefinition(string Kind, string Route);
