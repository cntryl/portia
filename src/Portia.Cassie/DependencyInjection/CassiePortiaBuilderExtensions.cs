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
    public static PortiaBuilder AddCassie(this PortiaBuilder builder, string connectionString,
        string checkpointTable = "portia_projection_checkpoints")
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        if (builder.Services.Any(static service => service.ServiceType == typeof(CassieProjectionDatabase)
            || service.ServiceType == typeof(CassieDataSource)))
            throw new InvalidOperationException("Cassie projection services are already configured.");
        builder.Services.AddSingleton(_ => new CassieDataSource(connectionString));
        builder.Services.AddSingleton(sp => new CassieProjectionDatabase(sp.GetRequiredService<CassieDataSource>(), checkpointTable));
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
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TProjector,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TRepository>(
        this PortiaBuilder builder, string name, WorkloadScope scope, Action<WorkloadOptions>? configure = null)
        where TProjector : Projector
        where TRepository : CassieProjectionRepository
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddScoped<TRepository>();
        return builder.AddProjector<TProjector>(name, scope, configure);
    }
}
