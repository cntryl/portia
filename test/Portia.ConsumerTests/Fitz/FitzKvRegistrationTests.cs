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
    public void FitzPublishesItsKvClientForApplicationRepositories()
    {
        var services = new ServiceCollection();

        _ = services.AddPortia().AddFitz(Configuration());

        var descriptor = Assert.Single(services, item => item.ServiceType == typeof(IKvClient));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    /// <summary>
    ///     Reactor checkpoints stay unregistered until the application asks for them, so the absence of
    ///     durable progress is a resolution failure at startup rather than silent replay after a restart.
    /// </summary>
    [Fact]
    public void ReactorCheckpointsAreNotRegisteredWithoutSelection()
    {
        var services = new ServiceCollection();

        _ = services.AddPortia().AddFitz(Configuration());

        Assert.DoesNotContain(services, item => item.ServiceType == typeof(IProjectionCheckpointStore));
    }

    [Fact]
    public void SelectingKvCheckpointsRegistersTheDurableStore()
    {
        var services = new ServiceCollection();

        _ = services.AddPortia().AddFitz(Configuration(), builder => builder.UseKvCheckpoints(Route));

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
    public void CheckpointRouteMustBeAnExactThreeSegmentKvRoute(string route)
    {
        var services = new ServiceCollection();

        var error = Assert.Throws<ArgumentException>(() =>
            services.AddPortia().AddFitz(Configuration(), builder => builder.UseKvCheckpoints(route)));

        Assert.Contains("kv://{realm}/{area}/{resource}", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     An API host and a worker host share one application setup method, so the same selection runs
    ///     more than once in a process. Repeating it is the normal case; changing it is the mistake.
    /// </summary>
    [Fact]
    public void RepeatingTheSameCheckpointRouteIsIdempotent()
    {
        var services = new ServiceCollection();

        _ = services.AddPortia().AddFitz(Configuration(), builder =>
            builder.UseKvCheckpoints(Route).UseKvCheckpoints(Route));

        _ = Assert.Single(services, item => item.ServiceType == typeof(IProjectionCheckpointStore));
    }

    [Fact]
    public void ConflictingCheckpointRoutesAreRejected()
    {
        var services = new ServiceCollection();

        var error = Assert.Throws<InvalidOperationException>(() =>
            services.AddPortia().AddFitz(Configuration(), builder =>
                builder.UseKvCheckpoints(Route).UseKvCheckpoints("kv://accounts/progress/other")));

        Assert.Contains("conflicting Fitz KV checkpoint routes", error.Message, StringComparison.Ordinal);
    }

    static IConfiguration Configuration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Endpoint"] = "ws://127.0.0.1:4090/ws" })
            .Build();
}
