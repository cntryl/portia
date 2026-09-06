using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

/// <summary>
/// Verifies that reactors and projectors declared in the compilation are registered into a
/// service collection by generated code, without runtime assembly scanning or reflection.
/// </summary>
public sealed class PortiaGeneratedServiceCollectionExtensionsTests
{
    /// <summary>
    /// Verifies that the request bus is resolvable after generated registration, so a consumer
    /// never has to remember to register it manually.
    /// </summary>
    [Fact]
    public void ShouldResolveRequestBusAfterGeneratedRegistration()
    {
        var services = new ServiceCollection();
        // A permission-guarded request exists somewhere in this compilation (see
        // RequestAuthorizationTests), so the generated bus needs an IPermissionEvaluator too —
        // not registered by AddPortiaGeneratedComponents itself, same as any other app-provided
        // dependency (IEventStore, IRequestSerializer, ...).
        _ = services.AddSingleton<IPermissionEvaluator>(TestPermissionEvaluator.AllowAll());
        _ = services.AddPortiaModule<FrameworkTestModule>();
        using var provider = services.BuildServiceProvider();

        var bus = provider.GetRequiredService<IRequestBus>();

        Assert.NotNull(bus);
    }

    /// <summary>
    /// Verifies that a concrete reactor is resolvable after generated registration.
    /// </summary>
    [Fact]
    public void ShouldResolveReactorAfterGeneratedRegistration()
    {
        var services = new ServiceCollection();
        _ = services.AddSingleton<IAggregateRepository>(new RecordingAggregateRepository());
        _ = services.AddPortiaModule<FrameworkTestModule>();
        using var provider = services.BuildServiceProvider();

        var reactor = provider.GetRequiredService<TestReactor>();

        Assert.NotNull(reactor);
    }

    /// <summary>
    /// Verifies that a concrete projector is resolvable after generated registration.
    /// </summary>
    [Fact]
    public void ShouldResolveProjectorAfterGeneratedRegistration()
    {
        var services = new ServiceCollection();
        _ = services.AddSingleton<IProjectionTarget<TestProjection>>(new UnusedProjectionTarget());
        _ = services.AddPortiaModule<FrameworkTestModule>();
        using var provider = services.BuildServiceProvider();

        var projector = provider.GetRequiredService<TestProjector>();

        Assert.NotNull(projector);
    }

    /// <summary>
    /// Verifies that the reactor registry lets a runner enumerate and resolve every reactor
    /// declared in this compilation without knowing its concrete type up front.
    /// </summary>
    [Fact]
    public void ShouldResolveReactorThroughReactorRegistration()
    {
        var services = new ServiceCollection();
        _ = services.AddSingleton<IAggregateRepository>(new RecordingAggregateRepository());
        _ = services.AddPortiaModule<FrameworkTestModule>();
        using var provider = services.BuildServiceProvider();

        var registration = Assert.Single(provider.GetServices<ReactorRegistration>(), r => r.ReactorType == typeof(TestReactor));
        var reactor = registration.Resolve(provider);

        _ = Assert.IsType<TestReactor>(reactor);
    }

    /// <summary>
    /// Verifies that the projector registry lets a runner enumerate and run every projector
    /// declared in this compilation without knowing its concrete or projection type up front.
    /// </summary>
    [Fact]
    public async Task ShouldRunProjectorThroughProjectorRegistration()
    {
        var id = Uuid.CreateVersion4();
        var stream = new EventStreamAddress("test", "projectors", id.ToString());
        var store = new InMemoryEventStore();
        var ev = new ValueChanged(42);
        ev.AttachMetadata(new DomainEventMetadata(Uuid.CreateVersion4(), id, 1, DateTimeOffset.UtcNow));
        await store.AppendAsync(stream, 0, [ev]);
        var target = new RecordingProjectionTarget();
        var services = new ServiceCollection();
        _ = services.AddSingleton<IProjectionTarget<TestProjection>>(target);
        _ = services.AddPortiaModule<FrameworkTestModule>();
        using var provider = services.BuildServiceProvider();
        var runner = new ProjectorRunner(store);

        var registration = Assert.Single(provider.GetServices<ProjectorRegistration>(), r => r.ProjectorType == typeof(TestProjector));
        _ = await registration.Run(runner, provider, ProjectionCheckpoint.Start, null, default);

        Assert.Equal(42, target.Projection.Value);
    }

    /// <summary>
    /// Verifies that every routed, transport-marked request declared in this compilation is
    /// registered with the transports it actually implements, and only those.
    /// </summary>
    [Fact]
    public void ShouldRegisterRequestTransportsMatchingDeclaredMarkers()
    {
        var services = new ServiceCollection();
        _ = services.AddSingleton<IAggregateRepository>(new RecordingAggregateRepository());
        _ = services.AddSingleton<IProjectionTarget<TestProjection>>(new UnusedProjectionTarget());
        _ = services.AddPortiaModule<FrameworkTestModule>();
        using var provider = services.BuildServiceProvider();
        var registrations = provider.GetServices<RequestTransportRegistration>().ToList();

        var createUser = Assert.Single(registrations, r => r.RequestType == typeof(CreateUser));
        Assert.Equal(RequestTransports.Callable, createUser.Transports);
        Assert.Equal("*", createUser.Route.Realm);
        Assert.Equal("identity", createUser.Route.Area);
        Assert.Equal("users", createUser.Route.Resource);
        Assert.Equal("create", createUser.Route.Operation);

        var sendWelcomeEmail = Assert.Single(registrations, r => r.RequestType == typeof(SendWelcomeEmail));
        Assert.Equal(
            RequestTransports.Callable | RequestTransports.Queuable | RequestTransports.Notifiable | RequestTransports.Schedulable,
            sendWelcomeEmail.Transports);
        Assert.Equal("welcome", sendWelcomeEmail.Route.Operation);
    }
}
