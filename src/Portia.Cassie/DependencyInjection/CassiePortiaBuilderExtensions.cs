using System.Diagnostics.CodeAnalysis;
using Cntryl.Cassie.Connection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Cntryl.Portia;

/// <summary>Connects native Cassie repositories to Portia's existing projector lifecycle.</summary>
public static class CassiePortiaBuilderExtensions
{
    /// <summary>Registers a shared Cassie data source and checkpoint metadata that a hosted application verifies at startup.</summary>
    /// <param name="builder">The application's Portia composition.</param>
    /// <param name="connectionString">The native Cassie connection string.</param>
    /// <param name="checkpointTable">The unqualified metadata table in the application's Cassie database.</param>
    /// <returns>The same builder for further composition.</returns>
    public static PortiaBuilder AddCassie(
        this PortiaBuilder builder,
        string connectionString,
        string checkpointTable = CassiePasswordProviderOptions.DefaultCheckpointTable
    ) => RegisterCassie(builder, connectionString, checkpointTable, null);

    /// <summary>Registers Cassie projections with a password supplied on each new physical connection.</summary>
    /// <param name="builder">The application's Portia composition.</param>
    /// <param name="options">The passwordless connection settings and host callback.</param>
    /// <returns>The same builder for further composition.</returns>
    public static PortiaBuilder AddCassie(this PortiaBuilder builder, CassiePasswordProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.PasswordProvider);
        return RegisterCassie(builder, options.ConnectionString, options.CheckpointTable, options.PasswordProvider);
    }

    static PortiaBuilder RegisterCassie(
        PortiaBuilder builder,
        string connectionString,
        string checkpointTable,
        Func<IServiceProvider, CancellationToken, ValueTask<string>>? passwordProvider
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        if (passwordProvider is not null)
        {
            CassieConnectionStringBuilder settings = [with(connectionString)];

            if (settings.ContainsKey("Password"))
                throw new ArgumentException(
                    "A password provider cannot be combined with a Password connection string entry.",
                    nameof(connectionString));
        }

        if (builder.Services.Any(static service => service.ServiceType == typeof(CassieProjectionDatabase)
                                                   || service.ServiceType == typeof(CassieDataSource)))
            throw new InvalidOperationException("Cassie projection services are already configured.");

        builder.Services.AddSingleton(provider =>
        {
            var clock = provider.GetService<TimeProvider>() ?? TimeProvider.System;
            return passwordProvider is null
                ? new CassieDataSource(connectionString, clock)
                : new CassieDataSource(connectionString, clock, token => passwordProvider(provider, token));
        });
        builder.Services.AddSingleton(sp =>
            new CassieProjectionDatabase(sp.GetRequiredService<CassieDataSource>(), checkpointTable));
        builder.Services.AddSingleton<IHostedService, CassieProjectionStartup>();
        return builder;
    }

    /// <summary>Registers a scoped application repository and a projector through Portia's standard runner.</summary>
    /// <typeparam name="TProjector">The application's typed projector.</typeparam>
    /// <typeparam name="TRepository">Its Cassie repository, also injectable by query handlers.</typeparam>
    /// <param name="builder">The application's Portia composition.</param>
    /// <param name="name">The stable projector name also supplied to the repository's base constructor.</param>
    /// <param name="scope">Whether this projector runs globally or once per tenant.</param>
    /// <param name="configure">Optional processing, polling, and retry settings.</param>
    /// <returns>The same builder for further composition.</returns>
    public static PortiaBuilder AddCassieProjection<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
    TProjector,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
    TRepository>(
        this PortiaBuilder builder,
        string name,
        WorkloadScope scope,
        Action<WorkloadOptions>? configure = null
    )
        where TProjector : Projector
        where TRepository : CassieProjectionRepository
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddScoped<TRepository>();
        return builder.AddProjector<TProjector>(name, scope, configure);
    }
}
