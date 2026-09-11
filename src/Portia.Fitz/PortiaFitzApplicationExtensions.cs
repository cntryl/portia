using System.Globalization;
using System.Runtime.CompilerServices;
using Cntryl.Fitz;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cntryl.Portia;

/// <summary>Connects shared Portia application setup to Fitz.</summary>
public static class PortiaFitzApplicationExtensions
{
    static readonly ConditionalWeakTable<PortiaBuilder, FitzSetup> Setups = [];

    /// <summary>Adds Fitz persistence, clients, coordination, and workers for every selected request transport.</summary>
    /// <param name="application">The application composition root to connect to Fitz.</param>
    /// <param name="configuration">The <c>Fitz</c> configuration section.</param>
    /// <returns>The same composition root, for chaining.</returns>
    public static PortiaBuilder AddFitz(this PortiaBuilder application, IConfiguration configuration) =>
        application.AddFitz(configuration, static _ => { });

    /// <summary>Configures an owned client from Endpoint and optional Token and StartupTimeoutSeconds settings.</summary>
    /// <param name="application">The application composition root to connect to Fitz.</param>
    /// <param name="configuration">
    ///     The <c>Fitz</c> configuration section, supplying <c>Endpoint</c> and
    ///     optionally <c>Token</c>, <c>StartupTimeoutSeconds</c>, and <c>ApplicationName</c>.
    /// </param>
    /// <param name="configure">Selects which workers run and how the fleet is composed.</param>
    /// <returns>The same composition root, for chaining.</returns>
    /// <exception cref="ArgumentException">A configured value is missing or out of range.</exception>
    public static PortiaBuilder AddFitz(this PortiaBuilder application, IConfiguration configuration,
        Action<PortiaFitzBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var endpoint = configuration["Endpoint"];
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("ws" or "wss"))
        {
            throw new ArgumentException("Fitz:Endpoint must be an absolute ws:// or wss:// URI.",
                nameof(configuration));
        }

        var token = configuration["Token"];
        var timeout = TimeSpan.FromSeconds(15);
        if (configuration["StartupTimeoutSeconds"] is { } text)
        {
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                || !double.IsFinite(seconds) || seconds <= 0 || seconds > 3600)
            {
                throw new ArgumentException("Fitz:StartupTimeoutSeconds must be between zero and 3600 seconds.",
                    nameof(configuration));
            }

            timeout = TimeSpan.FromSeconds(seconds);
        }

        var settings = new ClientConfig(uri, TokenProvider: token is null ? null : _ => ValueTask.FromResult(token));
        return Register(application, builder =>
        {
            if (configuration["ApplicationName"] is { } name)
            {
                if (!FleetRunOptions.IsSegment(name))
                {
                    throw new ArgumentException("Fitz:ApplicationName must be an exact route segment.",
                        nameof(configuration));
                }

                _ = builder.UseFleet(new FleetRunOptions { MembershipSelector = $"lease://{name}/portia-members/*" });
            }

            configure(builder);
        }, _ => new FitzApplicationConnection(new Client(settings), true, timeout), (uri, token, timeout));
    }

    /// <summary>Configures an owned client, including an optional rotating backend token provider.</summary>
    /// <param name="application">The application composition root to connect to Fitz.</param>
    /// <param name="configuration">The Fitz client settings.</param>
    /// <param name="startupTimeout">
    ///     How long the host waits for the connection, or
    ///     <see langword="null" /> for 15 seconds.
    /// </param>
    /// <returns>The same composition root, for chaining.</returns>
    public static PortiaBuilder AddFitz(
        this PortiaBuilder application,
        ClientConfig configuration,
        TimeSpan? startupTimeout = null) =>
        application.AddFitz(configuration, static _ => { }, startupTimeout);

    /// <summary>Configures an owned client with optional worker selection or fleet membership.</summary>
    /// <param name="application">The application composition root to connect to Fitz.</param>
    /// <param name="configuration">The Fitz client settings.</param>
    /// <param name="configure">Selects which workers run and how the fleet is composed.</param>
    /// <param name="startupTimeout">
    ///     How long the host waits for the connection, or
    ///     <see langword="null" /> for 15 seconds.
    /// </param>
    /// <returns>The same composition root, for chaining.</returns>
    public static PortiaBuilder AddFitz(this PortiaBuilder application, ClientConfig configuration,
        Action<PortiaFitzBuilder> configure, TimeSpan? startupTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var timeout = startupTimeout ?? TimeSpan.FromSeconds(15);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        return Register(application, configure,
            provider => new FitzApplicationConnection(new Client(configuration), true, timeout),
            (configuration, timeout));
    }

    /// <summary>Uses an already connected client and hosts every selected request transport.</summary>
    /// <param name="application">The application composition root to connect to Fitz.</param>
    /// <param name="client">The caller-owned client; Portia does not dispose it.</param>
    /// <returns>The same composition root, for chaining.</returns>
    public static PortiaBuilder UseFitzClient(this PortiaBuilder application, Client client) =>
        application.UseFitzClient(client, static _ => { });

    /// <summary>Uses an already connected client with optional worker selection or fleet membership.</summary>
    /// <param name="application">The application composition root to connect to Fitz.</param>
    /// <param name="client">The caller-owned client; Portia does not dispose it.</param>
    /// <param name="configure">Selects which workers run and how the fleet is composed.</param>
    /// <returns>The same composition root, for chaining.</returns>
    public static PortiaBuilder UseFitzClient(this PortiaBuilder application, Client client,
        Action<PortiaFitzBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(client);
        return Register(application, configure, _ => new FitzApplicationConnection(client, false, TimeSpan.Zero),
            client);
    }

    static PortiaBuilder Register(PortiaBuilder application, Action<PortiaFitzBuilder> configure,
        Func<IServiceProvider, FitzApplicationConnection> connection, object identity)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(configure);
        var services = application.Services;
        lock (application)
        {
            if (Setups.TryGetValue(application, out var existing))
            {
                if (!Equals(existing.Identity, identity))
                {
                    throw new InvalidOperationException(
                        "This application has conflicting Fitz connections. Configure one shared connection.");
                }
                else
                {
                    configure(existing.Builder);
                    return application;
                }
            }

            _ = services.AddSingleton(connection);
            _ = services.AddSingleton<IHostedService>(provider =>
                provider.GetRequiredService<FitzApplicationConnection>());
            var builder = new PortiaFitzBuilder(application);
            Setups.Add(application, new FitzSetup(identity, builder));
            services.TryAddSingleton<IWorkloadCoordinator>(provider => new FitzWorkloadCoordinator(
                provider.GetRequiredService<FitzApplicationConnection>(), builder,
                provider.GetService<ILogger<FleetPartitionRunner>>(),
                provider.GetService<TimeProvider>()));
            _ = builder.AddEventStore().AddKvClient().AddRequestClients();
            configure(builder);
            return application;
        }
    }

    sealed record FitzSetup(object Identity, PortiaFitzBuilder Builder);
}
