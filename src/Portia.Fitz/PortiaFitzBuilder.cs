using Cntryl.Fitz;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Cntryl.Portia;

/// <summary>Declares shared Fitz capabilities and worker-only listeners.</summary>
public sealed class PortiaFitzBuilder
{
    readonly PortiaBuilder _application;
    readonly HashSet<string> _capabilities = new(StringComparer.Ordinal);
    readonly List<FitzWorkerDefinition> _workers = [];
    readonly List<FitzComponentDefinition> _components = [];

    internal PortiaFitzBuilder(PortiaBuilder application)
    {
        _application = application;
        _ = application.ConfigureWorker("Portia.Fitz", services => _ = services.AddSingleton<IHostedService>(provider => new FitzApplicationWorkers(provider, this)));
    }

    internal IReadOnlyList<FitzWorkerDefinition> Workers => _workers;
    internal IReadOnlyList<FitzComponentDefinition> Components => _components;
    internal FleetRunOptions? Fleet { get; private set; }

    /// <summary>Registers event persistence, its reader/writer aliases, and JSON event serialization.</summary>
    public PortiaFitzBuilder AddEventStore()
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
        return this;
    }

    /// <summary>Registers outbound RPC, queue, notice, and scheduling clients without starting listeners.</summary>
    public PortiaFitzBuilder AddRequestClients()
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

    /// <summary>Declares RPC serving for callable requests in the included modules.</summary>
    public PortiaFitzBuilder AddRpcServer() => AddListener("rpc", "");

    /// <summary>Declares a competing queue worker for an explicit queue route.</summary>
    public PortiaFitzBuilder AddQueueWorker(string route) => AddListener("queue", route);

    /// <summary>Declares a notice fanout subscriber on each worker replica.</summary>
    public PortiaFitzBuilder AddNoticeWorker(string route) => AddListener("notice", route);

    /// <summary>Declares a schedule subscriber; the schedule's delivery mode controls distribution.</summary>
    public PortiaFitzBuilder AddScheduledWorker(string route) => AddListener("schedule", route);

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

    /// <summary>Declares a projector that runs only while this replica owns its explicit lease route.</summary>
    public PortiaFitzBuilder AddProjector<TProjector>(string leaseRoute, ProjectionRunOptions? options = null,
        TimeSpan? pollInterval = null) where TProjector : class
    {
        var settings = options ?? ProjectionRunOptions.Default;
        settings.Validate();
        return AddComponent(new FitzComponentDefinition(typeof(TProjector), leaseRoute, true, settings, Interval(pollInterval)));
    }

    /// <summary>Declares a reactor that runs only while this replica owns its explicit lease route.</summary>
    public PortiaFitzBuilder AddReactor<TReactor>(string leaseRoute, int maxBatchSize = 512,
        TimeSpan? pollInterval = null) where TReactor : Reactor
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBatchSize);
        return AddComponent(new FitzComponentDefinition(typeof(TReactor), leaseRoute, false,
            new ProjectionRunOptions { MaxBatchSize = maxBatchSize }, Interval(pollInterval)));
    }

    PortiaFitzBuilder AddComponent(FitzComponentDefinition definition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.LeaseRoute);
        var existing = _components.FirstOrDefault(component => component.Type == definition.Type || component.LeaseRoute == definition.LeaseRoute);
        if (existing is not null)
            return existing == definition ? this : throw new InvalidOperationException($"Conflicting component registration for '{definition.Type}' or lease '{definition.LeaseRoute}'.");
        _components.Add(definition);
        _application.Services.TryAddScoped<ProjectorRunner>();
        _application.Services.TryAddScoped<ReactorRunner>();
        _application.Services.TryAddScoped<WorkerLeaseContext>();
        return this;
    }

    PortiaFitzBuilder AddListener(string kind, string route)
    {
        if (kind != "rpc")
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(route);
            var segments = route.StartsWith(kind + "://", StringComparison.Ordinal) ? route[(kind.Length + 3)..].Split('/') : [];
            if (segments.Length != (kind == "schedule" ? 4 : 3) || segments.Any(string.IsNullOrWhiteSpace))
                throw new ArgumentException($"Invalid {kind} route '{route}'.", nameof(route));
        }
        if (_capabilities.Add(kind + ":" + route))
        {
            AddSerializers(_application.Services);
            _workers.Add(new FitzWorkerDefinition(kind, route));
        }
        return this;
    }

    static TimeSpan Interval(TimeSpan? value)
    {
        var interval = value ?? TimeSpan.FromSeconds(1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        return interval;
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
        return Register(application, configure, _ => new FitzApplicationConnection(new Client(settings), true, timeout), (uri, token, timeout));
    }

    /// <summary>Configures an owned client, including an optional rotating backend token provider.</summary>
    public static PortiaBuilder AddFitz(this PortiaBuilder application, ClientConfig configuration,
        Action<PortiaFitzBuilder> configure, TimeSpan? startupTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var timeout = startupTimeout ?? TimeSpan.FromSeconds(15);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        return Register(application, configure, provider => new FitzApplicationConnection(new Client(configuration), true, timeout), (configuration, timeout));
    }

    /// <summary>Uses an already connected client; its connection and disposal remain application-owned.</summary>
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
        configure(builder);
        return application;
    }

    sealed record FitzSetup(object Identity, PortiaFitzBuilder Builder);
}

sealed record FitzWorkerDefinition(string Kind, string Route);
sealed record FitzComponentDefinition(Type Type, string LeaseRoute, bool Projector, ProjectionRunOptions Options, TimeSpan Interval);
