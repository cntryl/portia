namespace Cntryl.Portia;

/// <summary>
///     Verifies <see cref="StoreFixture" /> composes a minimal Portia-backed service provider so tests don't
///     hand-wire an <see cref="IEventStore" /> and <c>AddPortia()</c> in every scenario.
/// </summary>
public sealed class StoreFixtureTests
{
    /// <summary>With no store supplied, the fixture defaults to an in-memory store.</summary>
    [Fact]
    public async Task ShouldUseInMemoryEventStoreGivenNoSuppliedStore()
    {
        // Arrange
        // The fixture is built with no store supplied.

        // Act
        await using var fixture = new StoreFixture();

        // Assert
        Assert.IsType<InMemoryEventStore>(fixture.Store);
    }

    /// <summary>A caller-supplied store is used instead of the default.</summary>
    [Fact]
    public async Task ShouldUseSuppliedEventStoreGivenSuppliedStore()
    {
        // Arrange
        var store = new InMemoryEventStore();

        // Act
        await using var fixture = new StoreFixture(store);

        // Assert
        Assert.Same(store, fixture.Store);
    }

    /// <summary>State saved through the fixture's repository hydrates back through it.</summary>
    [Fact]
    public async Task ShouldHydrateStateGivenStateSavedThroughRepository()
    {
        // Arrange
        await using var fixture = new StoreFixture();
        var id = Uuid.CreateVersion4();

        var aggregate = new TestAggregate(id);
        aggregate.ChangeValue(3);
        await fixture.Repository.SaveAsync(aggregate, new RequestDispatchContext(RequestActor.System));

        // Act
        var hydrated = await fixture.Repository.HydrateAsync(new TestAggregate(id));

        // Assert
        Assert.Equal(3, hydrated.Value);
    }

    /// <summary>The fixture's repository is also usable as the executor a handler-like class under test depends on.</summary>
    [Fact]
    public async Task ShouldExecuteAndHydrateGivenRepository()
    {
        // Arrange
        await using var fixture = new StoreFixture();
        var id = Uuid.CreateVersion4();

        await fixture.Repository.ExecuteAsync(new TestAggregate(id), aggregate =>
        {
            aggregate.ChangeValue(6);
            return AggregateOutcome.Commit(Result.Success);
        }, new RequestDispatchContext(RequestActor.System));

        // Act
        var hydrated = await fixture.Repository.HydrateAsync(new TestAggregate(id));

        // Assert
        Assert.Equal(6, hydrated.Value);
    }

    /// <summary>Anything else <c>AddPortia()</c> registers is reachable through the fixture's scope.</summary>
    [Fact]
    public async Task ShouldResolveAggregateExecutorGivenServices()
    {
        // Arrange
        await using var fixture = new StoreFixture();

        // Act
        var executor = fixture.Services.GetService(typeof(IAggregateExecutor));

        // Assert
        Assert.NotNull(executor);
    }
}
