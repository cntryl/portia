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
        var error = Assert.Throws<InvalidOperationException>(() => new RequestRegistry(
            [
                new RequestRegistration<UniversalAction, UniversalActionHandler>(),
                new RequestRegistration<UniversalAction, SecondUniversalActionHandler>()
            ],
            [], [], []));

        Assert.Contains("conflicting handlers", error.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(UniversalActionHandler), error.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(SecondUniversalActionHandler), error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Verifies that the same handler registered twice is not a conflict — a feature assembly whose
    ///     registrations are composed more than once must still start.
    /// </summary>
    [Fact]
    public void ShouldAcceptOneHandlerRegisteredTwice()
    {
        var registry = new RequestRegistry(
            [
                new RequestRegistration<UniversalAction, UniversalActionHandler>(),
                new RequestRegistration<UniversalAction, UniversalActionHandler>()
            ],
            [], [], []);

        Assert.NotNull(registry);
    }

    /// <summary>
    ///     Verifies that an authorization stage outside the defined set is refused at composition. An
    ///     undefined stage would sort into an arbitrary position around the declarative permission check,
    ///     which is the one ordering the request pipeline actually depends on.
    /// </summary>
    [Fact]
    public void ShouldRejectUndefinedAuthorizationStageAtRegistryComposition()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => new RequestRegistry([],
            [new RequestAuthorizerRegistration<UniversalAction, UniversalActionAuthorizer>((AuthorizationStage)42)],
            [], []));

        Assert.Contains("defined authorization stage", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Verifies that the builder refuses an undefined stage at the point of registration too, so the
    ///     mistake is reported where it was made rather than at startup.
    /// </summary>
    [Fact]
    public void ShouldRejectUndefinedAuthorizationStageAtRegistration()
    {
        var services = new ServiceCollection();
        var builder = services.AddPortia();
        var count = services.Count;

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
        var services = new ServiceCollection();
        var builder = services.AddPortia()
            .AddGeneratedAuthorizer(new RequestAuthorizerRegistration<UniversalAction, UniversalActionAuthorizer>());
        var second = new RequestAuthorizerRegistration<UniversalAction, UniversalActionAuthorizer>(
            conflicting ? AuthorizationStage.Principal : AuthorizationStage.ResourceAccess);

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
        var error = Assert.Throws<InvalidOperationException>(() => new RequestRegistry([],
            [
                new RequestAuthorizerRegistration<UniversalAction, UniversalActionAuthorizer>(
                    AuthorizationStage.Principal),
                new RequestAuthorizerRegistration<UniversalAction, UniversalActionAuthorizer>(
                    AuthorizationStage.ResourceAccess)
            ],
            [], []));

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
        var services = new ServiceCollection();
        var builder = services.AddPortia()
            .AddGeneratedBehavior(new RequestPipelineBehaviorRegistration<UniversalAction, CountingBehavior>(5));
        var second = new RequestPipelineBehaviorRegistration<UniversalAction, CountingBehavior>(conflicting ? 6 : 5);

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
        var error = Assert.Throws<InvalidOperationException>(() => new RequestRegistry([], [],
            [
                new RequestPipelineBehaviorRegistration<UniversalAction, CountingBehavior>(1),
                new RequestPipelineBehaviorRegistration<UniversalAction, CountingBehavior>(2)
            ], []));

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
        var services = new ServiceCollection();
        var builder = services.AddPortia()
            .AddProjector<NamedProjector>(WorkloadScope.Global, options => options.Name = "shared");

        var error = Assert.Throws<InvalidOperationException>(() =>
            builder.AddProjector<TestProjector>(WorkloadScope.Global, options => options.Name = "shared"));

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
        var services = new ServiceCollection();

        _ = services.AddPortia()
            .AddProjector<NamedProjector>(WorkloadScope.Global, options => options.Name = "first")
            .AddProjector<TestProjector>(WorkloadScope.Global, options => options.Name = "second");

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
        var services = new ServiceCollection();
        var builder = services.AddPortia();

        var error = Assert.Throws<ArgumentException>(() => builder.AddProjector<AbstractProjector>(
            WorkloadScope.Global));

        Assert.Contains("concrete, closed component type", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(services, item => item.ServiceType == typeof(WorkloadRegistration));
    }
}

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
