namespace Cntryl.Portia;

/// <summary>
///     Verifies <see cref="ProjectorScenario" /> runs a projector over given events through Portia's real runner,
///     committing progress to a store the scenario supplies, so the test asserts on its own read model.
/// </summary>
public sealed class ProjectorScenarioTests
{
    /// <summary>Finite histories drain beyond the runtime pass budget without gaps or duplicate effects.</summary>
    [Theory]
    [InlineData(4097)]
    [InlineData(8193)]
    public async Task ShouldDrainHistoryGivenBoundedPasses(int count)
    {
        // Arrange
        var ids = Enumerable.Range(0, count).Select(_ => Uuid.CreateVersion4()).ToArray();
        var scenario = new ProjectorScenario().Given(ids.Select(id => (DomainEvent)new UserCreated(id)).ToArray());
        var users = new List<Uuid>();
        var projector = new WelcomeProjector(users, scenario.Store);

        // Act
        await scenario.RunAsync(projector, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ids, users);
        var checkpoint =
            await scenario.Store.LoadCheckpointAsync(new CheckpointIdentity(projector.Name, projector.Pattern), TestContext.Current.CancellationToken);
        Assert.Equal(count.ToString(System.Globalization.CultureInfo.InvariantCulture), checkpoint.Cursor.ToString());
        await scenario.RunAsync(projector, TestContext.Current.CancellationToken);
        Assert.Equal(ids, users);
    }

    /// <summary>Every given event is projected, in order.</summary>
    [Fact]
    public async Task ShouldProjectGivenEventsInOrder()
    {
        // Arrange
        var first = Uuid.CreateVersion4();
        var second = Uuid.CreateVersion4();
        var users = new List<Uuid>();
        var scenario = new ProjectorScenario().Given(new UserCreated(first), new UserCreated(second));

        // Act
        await scenario.RunAsync(new WelcomeProjector(users, scenario.Store), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal([first, second], users);
    }

    /// <summary>A later run resumes from the committed checkpoint, projecting only events given since.</summary>
    [Fact]
    public async Task ShouldResumeGivenCommittedCheckpoint()
    {
        // Arrange
        var first = Uuid.CreateVersion4();
        var second = Uuid.CreateVersion4();
        var users = new List<Uuid>();
        var scenario = new ProjectorScenario();
        var projector = new WelcomeProjector(users, scenario.Store);

        await scenario.Given(new UserCreated(first)).RunAsync(projector, TestContext.Current.CancellationToken);

        // Act
        await scenario.Given(new UserCreated(second)).RunAsync(projector, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal([first, second], users);
    }

    /// <summary>A tenant-scoped projector runs bound to a test tenant, as its hosted workload would be.</summary>
    [Fact]
    public async Task ShouldRunProjectorGivenTenantScope()
    {
        // Arrange
        var user = Uuid.CreateVersion4();
        var users = new List<Uuid>();
        var scenario = new ProjectorScenario().Given(new UserCreated(user));

        // Act
        await scenario.RunAsync(new WelcomeProjector(users, scenario.Store, EventStreamPattern.ForTenant("users")), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal([user], users);
    }

    /// <summary>A caller-selected tenant binds the projector and supplies matching event streams.</summary>
    [Fact]
    public async Task ShouldRunProjectorGivenChosenTenant()
    {
        // Arrange
        var tenant = new TenantId(Uuid.CreateVersion4().ToString());
        var users = new List<Uuid>();
        var scenario = new ProjectorScenario(tenant).Given(new UserCreated(Uuid.CreateVersion4()));
        var projector = new WelcomeProjector(users, scenario.Store, EventStreamPattern.ForTenant("users"));

        // Act
        await scenario.RunAsync(projector, TestContext.Current.CancellationToken);

        // Assert
        Assert.Single(users);
        Assert.Equal(tenant.Value, projector.Pattern.Realm);
    }

    /// <summary>A chosen stream resource reaches the projector, so resource-dependent behavior is covered.</summary>
    [Fact]
    public async Task ShouldDeliverChosenResourceGivenStreamResource()
    {
        // Arrange
        var first = Uuid.CreateVersion4();
        var second = Uuid.CreateVersion4();
        var third = Uuid.CreateVersion4();
        var users = new List<Uuid>();
        var scenario = new ProjectorScenario()
            .Given("owner-a.record-1", new UserCreated(first))
            .Given("owner-b.record-2", new UserCreated(second))
            .Given("owner-a.record-1", new UserCreated(third));
        var projector = new WelcomeProjector(users, scenario.Store);

        // Act
        await scenario.RunAsync(projector, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal([first, second, third], users);
        Assert.Equal(["owner-a.record-1", "owner-b.record-2", "owner-a.record-1"], projector.StreamResources);
    }

    /// <summary>Bare events on a chosen resource form that stream's own aggregate, numbered from one.</summary>
    [Fact]
    public async Task ShouldNumberEventsPerStreamGivenStreamResources()
    {
        // Arrange
        var a1 = new UserCreated(Uuid.CreateVersion4());
        var b1 = new UserCreated(Uuid.CreateVersion4());
        var a2 = new UserCreated(Uuid.CreateVersion4());
        var bare = new UserCreated(Uuid.CreateVersion4());
        var scenario = new ProjectorScenario().Given("a", a1).Given("b", b1).Given("a", a2).Given(bare);

        // Act
        await scenario.RunAsync(new WelcomeProjector([], scenario.Store), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal([1UL, 1UL, 2UL, 1UL],
            new[] { a1, b1, a2, bare }.Select(ev => ev.Metadata.AggregateVersion));
        Assert.Equal(a1.Metadata.AggregateId, a2.Metadata.AggregateId);
        Assert.Equal(3, new[] { a1, b1, bare }.Select(ev => ev.Metadata.AggregateId).Distinct().Count());
    }

    /// <summary>
    ///     A chosen resource the projector's pattern excludes is rejected, because the projector would silently
    ///     never receive those events.
    /// </summary>
    [Fact]
    public async Task ShouldRejectRunGivenResourceOutsidePattern()
    {
        // Arrange
        var scenario = new ProjectorScenario().Given("other", new UserCreated(Uuid.CreateVersion4()));
        var projector = new WelcomeProjector([], scenario.Store, EventStreamPattern.ForPattern("test", "users", "one"));

        // Act
        var exception = await Record.ExceptionAsync(() => scenario.RunAsync(projector, TestContext.Current.CancellationToken));

        // Assert
        var error = Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("'other'", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A chosen resource must be a valid stream segment.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("a/b")]
    [InlineData("*")]
    public void ShouldRejectGivenGivenInvalidResource(string resource)
    {
        // Arrange
        var scenario = new ProjectorScenario();

        // Act
        var exception = Record.Exception(() => scenario.Given(resource, new UserCreated(Uuid.CreateVersion4())));

        // Assert
        _ = Assert.IsAssignableFrom<ArgumentException>(exception);
    }

    /// <summary>The optional tenant cannot be the uninitialized value of the tenant struct.</summary>
    [Fact]
    public void ShouldRejectScenarioGivenUninitializedTenant() =>
        _ = Assert.ThrowsAny<ArgumentException>(() => new ProjectorScenario(default));
}
