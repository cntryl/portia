using Cntryl.Fitz;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia.Consumer;

/// <summary>
///     Verifies how an application wires Fitz KV persistence. Both durable stores are constructed from
///     the shared connection rather than a second one the application opens itself, and reactor
///     progress is an explicit selection: a reactor that silently received an in-memory checkpoint
///     store would replay its entire backlog of effects after every restart.
/// </summary>
public sealed class FitzKvRegistrationTests
{
    const string Route = "kv://accounts/progress/checkpoints";

    /// <summary>
    ///     A repository deriving from <see cref="FitzKvProjectionStore" /> needs the same connection the
    ///     event store reads through, so its projection writes and its checkpoint share one transaction
    ///     against a broker the host already connects and disposes.
    /// </summary>
    [Fact]
    public void ShouldPublishKvClientGivenFitzRegistration()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        _ = services.AddPortia().AddFitz(Configuration());

        // Assert
        var descriptor = Assert.Single(services, item => item.ServiceType == typeof(IKvClient));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    /// <summary>
    ///     An application adapter that needs realm or global broker positions (a source-fact resolver, a
    ///     fixed-cut reader) reads through the same connection the event store uses, so it never opens a
    ///     second connection to learn what the first one already knows.
    /// </summary>
    [Fact]
    public void ShouldPublishStreamClientGivenFitzRegistration()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        _ = services.AddPortia().AddFitz(Configuration());

        // Assert
        var descriptor = Assert.Single(services, item => item.ServiceType == typeof(IStreamClient));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    /// <summary>
    ///     Reactor checkpoints stay unregistered until the application asks for them, so the absence of
    ///     durable progress is a resolution failure at startup rather than silent replay after a restart.
    /// </summary>
    [Fact]
    public void ShouldNotRegisterCheckpointStoreGivenNoCheckpointSelection()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        _ = services.AddPortia().AddFitz(Configuration());

        // Assert
        Assert.DoesNotContain(services, item => item.ServiceType == typeof(IProjectionCheckpointStore));
    }

    /// <summary>
    ///     Tenant directory snapshots are an explicit selection like reactor checkpoints: without one the
    ///     event-sourced directory replays its lifecycle history on every start, which is correct but slow.
    /// </summary>
    [Fact]
    public void ShouldRegisterSnapshotStoreGivenKvTenantSnapshotsSelected()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        _ = services.AddPortia().AddFitz(Configuration(),
            builder => builder.UseKvTenantSnapshots("kv://accounts/tenants/snapshots"));

        // Assert
        var descriptor = Assert.Single(services, item => item.ServiceType == typeof(ITenantDirectorySnapshotStore));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        Assert.DoesNotContain(services, item => item.ServiceType == typeof(IProjectionCheckpointStore));
    }

    /// <summary>Two different snapshot routes cannot both be the one a restart resumes from.</summary>
    [Fact]
    public void ShouldRejectSnapshotRoutesGivenConflictingTenantSnapshotRoutes()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        var error = Record.Exception(() => services.AddPortia().AddFitz(Configuration(), builder =>
            builder.UseKvTenantSnapshots("kv://accounts/tenants/snapshots")
                .UseKvTenantSnapshots("kv://accounts/tenants/other")));

        // Assert
        _ = Assert.IsType<InvalidOperationException>(error);
    }

    [Fact]
    public void ShouldRegisterCheckpointStoreGivenKvCheckpointsSelected()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        _ = services.AddPortia().AddFitz(Configuration(), builder => builder.UseKvCheckpoints(Route));

        // Assert
        var descriptor = Assert.Single(services, item => item.ServiceType == typeof(IProjectionCheckpointStore));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    [Theory]
    [InlineData("")]
    [InlineData("checkpoints")]
    [InlineData("stream://accounts/progress/checkpoints")]
    [InlineData("kv://accounts/progress")]
    [InlineData("kv://accounts/progress/checkpoints/extra")]
    [InlineData("kv://accounts//checkpoints")]
    [InlineData("kv://accounts/*/checkpoints")]
    public void ShouldRejectCheckpointRouteGivenRouteNotExactThreeSegmentKvRoute(string route)
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        var error = Record.Exception(() =>
            services.AddPortia().AddFitz(Configuration(), builder => builder.UseKvCheckpoints(route)));

        // Assert
        var invalid = Assert.IsType<ArgumentException>(error);
        Assert.Contains("kv://{realm}/{area}/{resource}", invalid.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     An API host and a worker host share one application setup method, so the same selection runs
    ///     more than once in a process. Repeating it is the normal case; changing it is the mistake.
    /// </summary>
    [Fact]
    public void ShouldRegisterOneCheckpointStoreGivenSameRouteRepeated()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        _ = services.AddPortia().AddFitz(Configuration(), builder =>
            builder.UseKvCheckpoints(Route).UseKvCheckpoints(Route));

        // Assert
        _ = Assert.Single(services, item => item.ServiceType == typeof(IProjectionCheckpointStore));
    }

    [Fact]
    public void ShouldRejectCheckpointRoutesGivenConflictingRoutes()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        var error = Record.Exception(() =>
            services.AddPortia().AddFitz(Configuration(), builder =>
                builder.UseKvCheckpoints(Route).UseKvCheckpoints("kv://accounts/progress/other")));

        // Assert
        var invalid = Assert.IsType<InvalidOperationException>(error);
        Assert.Contains("conflicting Fitz KV checkpoint routes", invalid.Message, StringComparison.Ordinal);
    }

    static IConfiguration Configuration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Endpoint"] = "ws://127.0.0.1:4090/ws" })
            .Build();
}
