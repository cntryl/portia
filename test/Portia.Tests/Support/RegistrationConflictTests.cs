using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

/// <summary>
///     Verifies the composition conflicts Portia refuses rather than resolving by registration order.
///     Two handlers for one request, one authorizer claiming two stages, one behavior claiming two
///     orders, or two components claiming one workload name are all cases where picking a winner would
///     work in development and behave differently in production, depending on which assembly happened
///     to register first. Each is a fail-fast, and each also has a benign twin that must still compose.
/// </summary>
public sealed class RegistrationConflictTests
{
    /// <summary>
    ///     Verifies that two different handlers for one request type are refused by the registry, naming
    ///     both, rather than the last registration silently winning.
    /// </summary>
    [Fact]
    public void ShouldRejectTwoHandlersForOneRequestAtRegistryComposition()
    {
        // Arrange
        RequestHandlerRegistration[] handlers =
        [
            new RequestRegistration<UniversalAction, UniversalActionHandler>(),
            new RequestRegistration<UniversalAction, SecondUniversalActionHandler>()
        ];

        // Act
        var error = Record.Exception(() => new RequestRegistry(handlers, [], [], []));

        // Assert
        _ = Assert.IsType<InvalidOperationException>(error);
        Assert.Contains("conflicting handlers", error.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(UniversalActionHandler), error.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(SecondUniversalActionHandler), error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Verifies that one type cannot be both a request authorizer and a request guard: authorization
    ///     and preflight are separate responsibilities that run at different lifecycle phases.
    /// </summary>
    [Fact]
    public void ShouldRejectTypeRegisteredAsBothAuthorizerAndGuard()
    {
        // Arrange
        var handlers = new[] { new RequestRegistration<UniversalAction, UniversalActionHandler>() };
        var authorizers = new[] { new RequestAuthorizerRegistration<UniversalAction, DualRoleComponent>() };
        var guards = new[] { new RequestGuardRegistration<UniversalAction, DualRoleComponent>() };

        // Act
        var error = Record.Exception(() => new RequestRegistry(handlers, authorizers, [], guards, []));

        // Assert
        _ = Assert.IsType<InvalidOperationException>(error);
        Assert.Contains(typeof(DualRoleComponent).FullName!, error.Message, StringComparison.Ordinal);
        Assert.Contains("request authorizer", error.Message, StringComparison.Ordinal);
        Assert.Contains("request guard", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Verifies that the same handler registered twice is not a conflict — a feature assembly whose
    ///     registrations are composed more than once must still start.
    /// </summary>
    [Fact]
    public void ShouldAcceptOneHandlerRegisteredTwice()
    {
        // Arrange
        RequestHandlerRegistration[] handlers =
        [
            new RequestRegistration<UniversalAction, UniversalActionHandler>(),
            new RequestRegistration<UniversalAction, UniversalActionHandler>()
        ];

        // Act
        var registry = new RequestRegistry(handlers, [], [], []);

        // Assert
        Assert.NotNull(registry);
    }

    /// <summary>Equivalent transport metadata composes idempotently at every runtime boundary.</summary>
    [Fact]
    public void ShouldAcceptEquivalentTransportDescriptors()
    {
        // Arrange
        var first = Transport();
        var second = Transport();
        var services = new ServiceCollection();

        _ = services.AddPortia().AddGeneratedRequest(first).AddGeneratedRequest(second);
        var registry = new RequestRegistry([], [], [], [first, second]);

        // Act
        var catalog = new RequestTransportCatalog([first, second]);

        // Assert
        _ = Assert.Single(services, item =>
            item.ImplementationInstance is RequestTransportRegistration registration &&
            registration.RequestType == typeof(TransportConflictRequest));
        Assert.NotNull(registry);
        Assert.Same(first, catalog.Get(typeof(TransportConflictRequest)));
    }

    /// <summary>Conflicting transport metadata fails before the builder mutates the service collection.</summary>
    [Fact]
    public void ShouldRejectConflictingTransportDescriptorsAtRegistration()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = services.AddPortia().AddGeneratedRequest(Transport());

        // Act
        var count = services.Count;

        // Assert
        var error = Assert.Throws<InvalidOperationException>(() =>
            builder.AddGeneratedRequest(Transport("different")));

        Assert.Contains("conflicting transport descriptors", error.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(TransportConflictRequest), error.Message, StringComparison.Ordinal);
        Assert.Equal(count, services.Count);
    }

    /// <summary>The registry rejects conflicting transport metadata independently of registration order.</summary>
    /// <param name="reverse">Whether the conflicting descriptor arrives first.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShouldRejectConflictingTransportDescriptorsAtRegistryComposition(bool reverse)
    {
        // Arrange
        var first = Transport();
        var second = Transport(discriminator: "different");

        // Act
        RequestTransportRegistration[] requests = reverse ? [second, first] : [first, second];

        // Assert
        var error = Assert.Throws<InvalidOperationException>(() => new RequestRegistry([], [], [], requests));

        Assert.Contains("conflicting transport descriptors", error.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(TransportConflictRequest), error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Verifies that an authorization stage outside the defined set is refused at composition. An
    ///     undefined stage would sort into an arbitrary position around the declarative permission check,
    ///     which is the one ordering the request pipeline actually depends on.
    /// </summary>
    [Fact]
    public void ShouldRejectUndefinedAuthorizationStageAtRegistryComposition()
    {
        // Arrange
        RequestAuthorizerRegistration[] authorizers =
            [new RequestAuthorizerRegistration<UniversalAction, UniversalActionAuthorizer>((AuthorizationStage)42)];

        // Act
        var error = Record.Exception(() => new RequestRegistry([], authorizers, [], []));

        // Assert
        _ = Assert.IsType<ArgumentOutOfRangeException>(error);
        Assert.Contains("defined authorization stage", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Verifies that the builder refuses an undefined stage at the point of registration too, so the
    ///     mistake is reported where it was made rather than at startup.
    /// </summary>
    [Fact]
    public void ShouldRejectUndefinedAuthorizationStageAtRegistration()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = services.AddPortia();

        // Act
        var count = services.Count;

        // Assert
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => builder.AddGeneratedAuthorizer(
            new RequestAuthorizerRegistration<UniversalAction, UniversalActionAuthorizer>((AuthorizationStage)42)));

        Assert.Equal(count, services.Count);
    }

    /// <summary>
    ///     Verifies that one authorizer cannot be registered at two different stages, and that
    ///     re-registering it at the same stage is accepted.
    /// </summary>
    /// <param name="conflicting">Whether the second registration names a different stage.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShouldRejectOnlyConflictingAuthorizerStages(bool conflicting)
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = services.AddPortia()
            .AddGeneratedAuthorizer(new RequestAuthorizerRegistration<UniversalAction, UniversalActionAuthorizer>());

        // Act
        var second = new RequestAuthorizerRegistration<UniversalAction, UniversalActionAuthorizer>(
            conflicting ? AuthorizationStage.Principal : AuthorizationStage.ResourceAccess);

        // Assert
        if (conflicting)
        {
            var error = Assert.Throws<InvalidOperationException>(() => builder.AddGeneratedAuthorizer(second));
            Assert.Contains("conflicting stages", error.Message, StringComparison.Ordinal);
        }
        else
        {
            _ = builder.AddGeneratedAuthorizer(second);
            _ = Assert.Single(services, item => item.ServiceType == typeof(RequestAuthorizerRegistration));
        }
    }

    /// <summary>
    ///     Verifies the same rule for the registry, which composes whatever reached the container: one
    ///     authorizer at two stages is refused there too, naming the authorizer.
    /// </summary>
    [Fact]
    public void ShouldRejectConflictingAuthorizerStagesAtRegistryComposition()
    {
        // Arrange
        RequestAuthorizerRegistration[] authorizers =
        [
            new RequestAuthorizerRegistration<UniversalAction, UniversalActionAuthorizer>(
                AuthorizationStage.Principal),
            new RequestAuthorizerRegistration<UniversalAction, UniversalActionAuthorizer>()
        ];

        // Act
        var error = Record.Exception(() => new RequestRegistry([], authorizers, [], []));

        // Assert
        _ = Assert.IsType<InvalidOperationException>(error);
        Assert.Contains("conflicting stages", error.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(UniversalActionAuthorizer), error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Verifies that one behavior cannot claim two positions in the pipeline, and that repeating the
    ///     same order composes. Order decides what wraps what, so an arbitrary winner would silently
    ///     change which behavior sees a short-circuit.
    /// </summary>
    /// <param name="conflicting">Whether the second registration names a different order.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShouldRejectOnlyConflictingBehaviorOrders(bool conflicting)
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = services.AddPortia()
            .AddGeneratedBehavior(new RequestPipelineBehaviorRegistration<UniversalAction, CountingBehavior>(5));

        // Act
        var second = new RequestPipelineBehaviorRegistration<UniversalAction, CountingBehavior>(conflicting ? 6 : 5);

        // Assert
        if (conflicting)
        {
            var error = Assert.Throws<InvalidOperationException>(() => builder.AddGeneratedBehavior(second));
            Assert.Contains("conflicting orders", error.Message, StringComparison.Ordinal);
        }
        else
        {
            _ = builder.AddGeneratedBehavior(second);
            _ = Assert.Single(services, item => item.ServiceType == typeof(RequestPipelineBehaviorRegistration));
        }
    }

    /// <summary>
    ///     Verifies the registry refuses conflicting behavior orders as well, naming the behavior.
    /// </summary>
    [Fact]
    public void ShouldRejectConflictingBehaviorOrdersAtRegistryComposition()
    {
        // Arrange
        RequestPipelineBehaviorRegistration[] behaviors =
        [
            new RequestPipelineBehaviorRegistration<UniversalAction, CountingBehavior>(1),
            new RequestPipelineBehaviorRegistration<UniversalAction, CountingBehavior>(2)
        ];

        // Act
        var error = Record.Exception(() => new RequestRegistry([], [], behaviors, []));

        // Assert
        _ = Assert.IsType<InvalidOperationException>(error);
        Assert.Contains("conflicting orders", error.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(CountingBehavior), error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Verifies that two different components cannot claim one workload name. The name is the
    ///     component's checkpoint identity, so sharing it would silently make two projectors resume from
    ///     each other's progress — the same hazard workload naming already guards against elsewhere.
    /// </summary>
    [Fact]
    public void ShouldRejectTwoComponentsClaimingOneWorkloadName()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        var builder = services.AddPortia()
            .AddProjector<NamedProjector>("shared", WorkloadScope.Global);

        // Assert
        var error = Assert.Throws<InvalidOperationException>(() =>
            builder.AddProjector<TestProjector>("shared", WorkloadScope.Global));

        Assert.Contains("Conflicting workload registration", error.Message, StringComparison.Ordinal);
        Assert.Contains("shared", error.Message, StringComparison.Ordinal);
        _ = Assert.Single(services, item => item.ServiceType == typeof(WorkloadRegistration));
    }

    /// <summary>
    ///     Verifies that two components with distinct names compose, so the rule above bounds collisions
    ///     rather than the number of components one application can host.
    /// </summary>
    [Fact]
    public void ShouldAcceptTwoComponentsWithDistinctWorkloadNames()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        _ = services.AddPortia()
            .AddProjector<NamedProjector>("first", WorkloadScope.Global)
            .AddProjector<TestProjector>("second", WorkloadScope.Global);

        // Assert
        Assert.Equal(2, services.Count(item => item.ServiceType == typeof(WorkloadRegistration)));
    }

    /// <summary>
    ///     Verifies that an abstract component is refused at registration. The generic constraint admits
    ///     one — an abstract projector is still a projector — but nothing can construct it, so accepting
    ///     the registration would produce a workload that fails only once the host first starts it.
    /// </summary>
    [Fact]
    public void ShouldRejectAbstractComponent()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        var builder = services.AddPortia();

        // Assert
        var error = Assert.Throws<ArgumentException>(() =>
            builder.AddProjector<AbstractProjector>("AbstractProjector", WorkloadScope.Global));

        Assert.Contains("concrete, closed component type", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(services, item => item.ServiceType == typeof(WorkloadRegistration));
    }

    static RequestTransportRegistration Transport(string operation = "execute",
        string discriminator = "transport-conflict") => new(typeof(TransportConflictRequest),
        [RequestTransportId.Callable],
        new RequestRouteAttribute("tests", "requests", "universal", operation),
        new DiscriminatorAttribute(discriminator));
}

sealed record TransportConflictRequest : IRequest;

// A projector that cannot be constructed, used only to be refused.
abstract class AbstractProjector(IProjectionStore target)
    : Projector(target, EventStreamPattern.ForPattern("test", "abstract"), "abstract-projector");

// A second handler for the same request, used only to collide with the first.
sealed class SecondUniversalActionHandler : IRequestHandler<UniversalAction>
{
    public ValueTask<Result> HandleAsync(IRequestContext<UniversalAction> context, CancellationToken ct) =>
        ValueTask.FromResult(Result.Success);
}

// An authorizer used only to exercise stage conflicts.
sealed class UniversalActionAuthorizer : IRequestAuthorizer<UniversalAction>
{
    public ValueTask<Result> AuthorizeAsync(IRequestContext<UniversalAction> context, CancellationToken ct) =>
        ValueTask.FromResult(Result.Success);
}

// A behavior used only to exercise order conflicts.
sealed class CountingBehavior : IRequestPipelineBehavior<UniversalAction>
{
    public ValueTask<Result> HandleAsync(IRequestContext<UniversalAction> context, RequestPipelineNext next,
        CancellationToken ct) => next(ct);
}

// A component that wrongly claims both the authorizer and the guard role.
sealed class DualRoleComponent : IRequestAuthorizer<UniversalAction>, IRequestGuard<UniversalAction>
{
    public ValueTask<Result> AuthorizeAsync(IRequestContext<UniversalAction> context, CancellationToken ct) =>
        ValueTask.FromResult(Result.Success);

    public ValueTask<Result> GuardAsync(IRequestContext<UniversalAction> context, CancellationToken ct) =>
        ValueTask.FromResult(Result.Success);
}
