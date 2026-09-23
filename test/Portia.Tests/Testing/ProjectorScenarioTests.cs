namespace Cntryl.Portia;

/// <summary>
///     Verifies <see cref="ProjectorScenario" /> runs a projector over given events through Portia's real runner,
///     committing progress to a store the scenario supplies, so the test asserts on its own read model.
/// </summary>
public sealed class ProjectorScenarioTests
{
    /// <summary>Every given event is projected, in order.</summary>
    [Fact]
    public async Task ShouldProjectGivenEventsInOrder()
    {
        var first = Uuid.CreateVersion4();
        var second = Uuid.CreateVersion4();
        var users = new List<Uuid>();
        var scenario = new ProjectorScenario().Given(new UserCreated(first), new UserCreated(second));

        await scenario.RunAsync(new WelcomeProjector(users, scenario.Store));

        Assert.Equal([first, second], users);
    }

    /// <summary>A later run resumes from the committed checkpoint, projecting only events given since.</summary>
    [Fact]
    public async Task ShouldResumeFromCommittedCheckpoint()
    {
        var first = Uuid.CreateVersion4();
        var second = Uuid.CreateVersion4();
        var users = new List<Uuid>();
        var scenario = new ProjectorScenario();
        var projector = new WelcomeProjector(users, scenario.Store);

        await scenario.Given(new UserCreated(first)).RunAsync(projector);
        await scenario.Given(new UserCreated(second)).RunAsync(projector);

        Assert.Equal([first, second], users);
    }

    /// <summary>A tenant-scoped projector runs bound to a test tenant, as its hosted workload would be.</summary>
    [Fact]
    public async Task ShouldRunTenantScopedProjector()
    {
        var user = Uuid.CreateVersion4();
        var users = new List<Uuid>();
        var scenario = new ProjectorScenario().Given(new UserCreated(user));

        await scenario.RunAsync(new WelcomeProjector(users, scenario.Store, EventStreamPattern.ForTenant("users")));

        Assert.Equal([user], users);
    }
}
