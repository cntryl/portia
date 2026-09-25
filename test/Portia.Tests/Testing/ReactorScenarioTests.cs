namespace Cntryl.Portia;

/// <summary>
///     Verifies <see cref="ReactorScenario" /> runs a reactor over given events through Portia's real runner and
///     records the requests it sent, so reactor tests need no event-store seeding or hand-written request bus.
/// </summary>
public sealed class ReactorScenarioTests
{
    /// <summary>Each given event reaches the reactor, and every request it sends is recorded in order.</summary>
    [Fact]
    public async Task ShouldRecordRequestsSentForEachGivenEvent()
    {
        var scenario = new ReactorScenario();
        var reactor = new WelcomeReactor(scenario.Requests);
        var first = Uuid.CreateVersion4();
        var second = Uuid.CreateVersion4();

        await scenario.Given(new UserCreated(first), new UserCreated(second)).RunAsync(reactor);

        Assert.Equal<IRequestBase>([new SendWelcomeEmail(first), new SendWelcomeEmail(second)],
            scenario.SentRequests);
    }

    /// <summary>Events from different aggregates are delivered in the order they were given.</summary>
    [Fact]
    public async Task ShouldDeliverEventsFromSeveralAggregatesInGivenOrder()
    {
        var first = Uuid.CreateVersion4();
        var second = Uuid.CreateVersion4();
        var scenario = new ReactorScenario().Given(
            DomainEventSeed.Attach(new UserCreated(first), Uuid.CreateVersion4(), 1),
            DomainEventSeed.Attach(new UserCreated(second), Uuid.CreateVersion4(), 1),
            new UserCreated(first));

        await scenario.RunAsync(new WelcomeReactor(scenario.Requests));

        Assert.Equal<IRequestBase>(
            [new SendWelcomeEmail(first), new SendWelcomeEmail(second), new SendWelcomeEmail(first)],
            scenario.SentRequests);
    }

    /// <summary>A scripted failure surfaces as the reaction failure the runner would retry.</summary>
    [Fact]
    public async Task ShouldFailReactionWhenScriptedRequestFails()
    {
        var scenario = new ReactorScenario()
            .Given(new UserCreated(Uuid.CreateVersion4()))
            .RespondTo<SendWelcomeEmail>(Result.Failure(new RequestError(RequestErrorKind.Conflict, "already welcomed")));

        var error = await Assert.ThrowsAsync<ReactionCommandFailedException>(async () =>
            await scenario.RunAsync(new WelcomeReactor(scenario.Requests)));

        Assert.Equal(RequestErrorKind.Conflict, error.Error.Kind);
        _ = Assert.Single(scenario.SentRequests);
    }

    /// <summary>
    ///     Scripting an abstract request type is rejected, because responses match the request's exact type and
    ///     such a script would silently never apply.
    /// </summary>
    [Fact]
    public void ShouldRejectScriptForNonConcreteRequestType() =>
        _ = Assert.Throws<ArgumentException>(() => new ReactorScenario().RespondTo<IRequest>(Result.Success));

    /// <summary>Running again redelivers the same events, so replay tolerance is testable without a failure.</summary>
    [Fact]
    public async Task ShouldRedeliverGivenEventsOnEachRun()
    {
        var scenario = new ReactorScenario().Given(new UserCreated(Uuid.CreateVersion4()));
        var reactor = new WelcomeReactor(scenario.Requests);

        await scenario.RunAsync(reactor);
        await scenario.RunAsync(reactor);

        Assert.Equal(2, scenario.SentRequests.Count);
        Assert.Equal(scenario.SentRequests[0], scenario.SentRequests[1]);
    }

    /// <summary>A tenant-scoped reactor runs bound to a test tenant, as its hosted workload would be.</summary>
    [Fact]
    public async Task ShouldRunTenantScopedReactor()
    {
        var user = Uuid.CreateVersion4();
        var scenario = new ReactorScenario().Given(new UserCreated(user));

        await scenario.RunAsync(new WelcomeReactor(scenario.Requests, EventStreamPattern.ForTenant("users")));

        Assert.Equal<IRequestBase>([new SendWelcomeEmail(user)], scenario.SentRequests);
    }

    /// <summary>A caller-selected tenant binds the reactor and supplies matching event streams.</summary>
    [Fact]
    public async Task ShouldRunReactorUnderChosenTenant()
    {
        var tenant = new TenantId(Uuid.CreateVersion4().ToString());
        var user = Uuid.CreateVersion4();
        var scenario = new ReactorScenario(tenant).Given(new UserCreated(user));
        var reactor = new WelcomeReactor(scenario.Requests, EventStreamPattern.ForTenant("users"));

        await scenario.RunAsync(reactor);

        Assert.Equal(tenant.Value, reactor.Pattern.Realm);
        Assert.Equal(tenant.Value, reactor.LastStreamRealm);
        Assert.Equal<IRequestBase>([new SendWelcomeEmail(user)], scenario.SentRequests);
    }

    /// <summary>The optional tenant cannot be the uninitialized value of the tenant struct.</summary>
    [Fact]
    public void ShouldRejectUninitializedTenant() =>
        _ = Assert.ThrowsAny<ArgumentException>(() => new ReactorScenario(default));

    /// <summary>Seeded events reach the reactor with the metadata the test attached.</summary>
    [Fact]
    public async Task ShouldDeliverSeededEventsUnchanged()
    {
        var eventId = Uuid.CreateVersion4();
        var scenario = new ReactorScenario()
            .Given(DomainEventSeed.Attach(new UserCreated(Uuid.CreateVersion4()), Uuid.CreateVersion4(), 1,
                eventId: eventId));
        var reactor = new WelcomeReactor(scenario.Requests);

        await scenario.RunAsync(reactor);

        Assert.Equal(eventId, reactor.LastEventId);
    }
}
