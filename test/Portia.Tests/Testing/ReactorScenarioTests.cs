namespace Cntryl.Portia;

/// <summary>
///     Verifies <see cref="ReactorScenario" /> runs a reactor over given events through Portia's real runner and
///     records the requests it sent, so reactor tests need no event-store seeding or hand-written request bus.
/// </summary>
public sealed class ReactorScenarioTests
{
    /// <summary>Finite histories drain beyond the runtime pass budget without gaps or duplicate effects.</summary>
    [Theory]
    [InlineData(4097)]
    [InlineData(8193)]
    public async Task ShouldDrainHistoryGivenBoundedPasses(int count)
    {
        // Arrange
        var ids = Enumerable.Range(0, count).Select(_ => Uuid.CreateVersion4()).ToArray();
        var scenario = new ReactorScenario().Given(ids.Select(id => (DomainEvent)new UserCreated(id)).ToArray());
        var reactor = new WelcomeReactor(scenario.Requests);

        // Act
        await scenario.RunAsync(reactor, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ids.Select(id => (IRequestBase)new SendWelcomeEmail(id)), scenario.SentRequests);
        await scenario.RunAsync(reactor, TestContext.Current.CancellationToken);
        Assert.Equal(ids.Concat(ids).Select(id => (IRequestBase)new SendWelcomeEmail(id)), scenario.SentRequests);
    }

    /// <summary>Each given event reaches the reactor, and every request it sends is recorded in order.</summary>
    [Fact]
    public async Task ShouldRecordRequestsSentForEachGivenEvent()
    {
        // Arrange
        var scenario = new ReactorScenario();
        var reactor = new WelcomeReactor(scenario.Requests);
        var first = Uuid.CreateVersion4();
        var second = Uuid.CreateVersion4();

        // Act
        await scenario.Given(new UserCreated(first), new UserCreated(second)).RunAsync(reactor, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal<IRequestBase>([new SendWelcomeEmail(first), new SendWelcomeEmail(second)],
            scenario.SentRequests);
    }

    /// <summary>Events from different aggregates are delivered in the order they were given.</summary>
    [Fact]
    public async Task ShouldDeliverEventsFromSeveralAggregatesInGivenOrder()
    {
        // Arrange
        var first = Uuid.CreateVersion4();
        var second = Uuid.CreateVersion4();
        var scenario = new ReactorScenario().Given(
            DomainEventSeed.Attach(new UserCreated(first), Uuid.CreateVersion4(), 1),
            DomainEventSeed.Attach(new UserCreated(second), Uuid.CreateVersion4(), 1),
            new UserCreated(first));

        // Act
        await scenario.RunAsync(new WelcomeReactor(scenario.Requests), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal<IRequestBase>(
            [new SendWelcomeEmail(first), new SendWelcomeEmail(second), new SendWelcomeEmail(first)],
            scenario.SentRequests);
    }

    /// <summary>A request that expects a result returns the value scripted for it and is recorded.</summary>
    [Fact]
    public async Task ShouldReturnScriptedValueGivenResultRequest()
    {
        // Arrange
        var scenario = new ReactorScenario().RespondTo<GetValue, int>(Result<int>.Success(5));

        // Act
        var result = await scenario.Requests.SendAsync(new GetValue(), RequestActor.System, ct: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(5, result.Value);
        Assert.Equal<IRequestBase>([new GetValue()], scenario.SentRequests);
    }

    /// <summary>
    ///     A result request with nothing scripted names the missing script, because no value can be invented
    ///     for it.
    /// </summary>
    [Fact]
    public async Task ShouldNameMissingScriptGivenUnscriptedResultRequest()
    {
        // Arrange
        var scenario = new ReactorScenario();

        // Act
        var exception = await Record.ExceptionAsync(async () =>
            await scenario.Requests.SendAsync(new GetValue(), RequestActor.System, ct: TestContext.Current.CancellationToken));

        // Assert
        var error = Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains(nameof(ReactorScenario.RespondTo), error.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(GetValue), error.Message, StringComparison.Ordinal);
    }

    /// <summary>An exact request script takes precedence over a default for its result type.</summary>
    [Theory]
    [InlineData(false, 42)]
    [InlineData(true, 5)]
    public async Task ShouldResolveExplicitTypedDefaultGivenUnscriptedRequest(bool exact, int expected)
    {
        // Arrange
        var scenario = new ReactorScenario().RespondByDefault<int>(Result<int>.Success(42));
        if (exact)
            _ = scenario.RespondTo<GetValue, int>(Result<int>.Success(5));

        // Act
        var result = await scenario.Requests.SendAsync(new GetValue(), RequestActor.System, ct: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(expected, result.Value);
    }

    /// <summary>A type-erased unary send still uses the typed request path and preserves failure.</summary>
    [Fact]
    public async Task ShouldPreserveFailureGivenTypeErasedUnaryRequest()
    {
        // Arrange
        var error = RequestError.Conflict("Participant unavailable.");
        var scenario = new ReactorScenario().RespondTo<GetValue, int>(Result<int>.Failure(error));
        IUnaryRequest request = new GetValue();

        // Act
        var result = await scenario.Requests.SendAsync(request, RequestActor.System, ct: TestContext.Current.CancellationToken);

        // Assert
        Assert.Same(error, result.Error);
        Assert.Equal<IRequestBase>([request], scenario.SentRequests);
    }

    /// <summary>A stream request yields the items scripted for it.</summary>
    [Fact]
    public async Task ShouldYieldScriptedItemsGivenStreamRequest()
    {
        // Arrange
        var scenario = new ReactorScenario().RespondToStream<GuardedSequence, int>(1, 2, 3);

        // Act
        var items = await scenario.Requests.StreamAsync(new GuardedSequence(), RequestActor.System, ct: TestContext.Current.CancellationToken).ToListAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal([1, 2, 3], items);
    }

    /// <summary>
    ///     Asking the scenario bus to authorize reports a scripted denial rather than approving a request
    ///     whose authorization it never ran.
    /// </summary>
    [Fact]
    public async Task ShouldReportScriptedDenialGivenAuthorizationAsked()
    {
        // Arrange
        var scenario = new ReactorScenario().RespondTo<SendWelcomeEmail>(RequestError.Forbidden("not allowed"));
        var request = new SendWelcomeEmail(Uuid.CreateVersion4());

        // Act
        var result = await scenario.Requests.AuthorizeAsync(request,
            scenario.Requests.CreateContext(RequestActor.System), TestContext.Current.CancellationToken);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(RequestErrorKind.Forbidden, result.Error.Kind);
    }

    /// <summary>A scripted failure surfaces as the reaction failure the runner would retry.</summary>
    [Fact]
    public async Task ShouldFailReactionGivenScriptedRequestFailure()
    {
        // Arrange
        var scenario = new ReactorScenario()
            .Given(new UserCreated(Uuid.CreateVersion4()))
            .RespondTo<SendWelcomeEmail>(
                Result.Failure(new RequestError(RequestErrorKind.Conflict, "already welcomed")));

        // Act
        var exception = await Record.ExceptionAsync(async () =>
            await scenario.RunAsync(new WelcomeReactor(scenario.Requests), TestContext.Current.CancellationToken));

        // Assert
        var error = Assert.IsType<ProcessorFailureException>(exception);
        Assert.Equal(RequestErrorKind.Conflict, error.Error!.Kind);
        _ = Assert.Single(scenario.SentRequests);
    }

    /// <summary>
    ///     Scripting an abstract request type is rejected, because responses match the request's exact type and
    ///     such a script would silently never apply.
    /// </summary>
    [Fact]
    public void ShouldRejectScriptGivenNonConcreteRequestType()
    {
        // Arrange
        var scenario = new ReactorScenario();

        // Act
        var exception = Record.Exception(() => scenario.RespondTo<IRequest>(Result.Success));

        // Assert
        _ = Assert.IsType<ArgumentException>(exception);
    }

    /// <summary>Running again redelivers the same events, so replay tolerance is testable without a failure.</summary>
    [Fact]
    public async Task ShouldRedeliverGivenEventsOnEachRun()
    {
        // Arrange
        var scenario = new ReactorScenario().Given(new UserCreated(Uuid.CreateVersion4()));
        var reactor = new WelcomeReactor(scenario.Requests);

        await scenario.RunAsync(reactor, TestContext.Current.CancellationToken);

        // Act
        await scenario.RunAsync(reactor, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, scenario.SentRequests.Count);
        Assert.Equal(scenario.SentRequests[0], scenario.SentRequests[1]);
    }

    /// <summary>A tenant-scoped reactor runs bound to a test tenant, as its hosted workload would be.</summary>
    [Fact]
    public async Task ShouldRunReactorGivenTenantScope()
    {
        // Arrange
        var user = Uuid.CreateVersion4();
        var scenario = new ReactorScenario().Given(new UserCreated(user));

        // Act
        await scenario.RunAsync(new WelcomeReactor(scenario.Requests, EventStreamPattern.ForTenant("users")), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal<IRequestBase>([new SendWelcomeEmail(user)], scenario.SentRequests);
    }

    /// <summary>A caller-selected tenant binds the reactor and supplies matching event streams.</summary>
    [Fact]
    public async Task ShouldRunReactorGivenChosenTenant()
    {
        // Arrange
        var tenant = new TenantId(Uuid.CreateVersion4().ToString());
        var user = Uuid.CreateVersion4();
        var scenario = new ReactorScenario(tenant).Given(new UserCreated(user));
        var reactor = new WelcomeReactor(scenario.Requests, EventStreamPattern.ForTenant("users"));

        // Act
        await scenario.RunAsync(reactor, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(tenant.Value, reactor.Pattern.Realm);
        Assert.Equal(tenant.Value, reactor.LastStreamRealm);
        Assert.Equal<IRequestBase>([new SendWelcomeEmail(user)], scenario.SentRequests);
    }

    /// <summary>The optional tenant cannot be the uninitialized value of the tenant struct.</summary>
    [Fact]
    public void ShouldRejectScenarioGivenUninitializedTenant()
    {
        // Arrange
        var tenant = default(TenantId);

        // Act
        var exception = Record.Exception(() => new ReactorScenario(tenant));

        // Assert
        _ = Assert.IsAssignableFrom<ArgumentException>(exception);
    }

    /// <summary>A chosen stream resource reaches the reactor alongside scripted command responses.</summary>
    [Fact]
    public async Task ShouldDeliverChosenResourceGivenStreamResource()
    {
        // Arrange
        var first = Uuid.CreateVersion4();
        var second = Uuid.CreateVersion4();
        var scenario = new ReactorScenario()
            .Given("owner-a.record-1", new UserCreated(first))
            .Given("owner-b.record-2", new UserCreated(second))
            .RespondTo<SendWelcomeEmail>(Result.Success);
        var reactor = new WelcomeReactor(scenario.Requests);

        // Act
        await scenario.RunAsync(reactor, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(["owner-a.record-1", "owner-b.record-2"], reactor.StreamResources);
        Assert.Equal<IRequestBase>([new SendWelcomeEmail(first), new SendWelcomeEmail(second)],
            scenario.SentRequests);
    }

    /// <summary>
    ///     A chosen resource the reactor's pattern excludes is rejected, because the reactor would silently never
    ///     receive those events.
    /// </summary>
    [Fact]
    public async Task ShouldRejectRunGivenResourceOutsidePattern()
    {
        // Arrange
        var scenario = new ReactorScenario().Given("other", new UserCreated(Uuid.CreateVersion4()));
        var reactor = new WelcomeReactor(scenario.Requests, EventStreamPattern.ForPattern("test", "users", "one"));

        // Act
        var exception = await Record.ExceptionAsync(() => scenario.RunAsync(reactor, TestContext.Current.CancellationToken));

        // Assert
        var error = Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("'other'", error.Message, StringComparison.Ordinal);
        Assert.Empty(scenario.SentRequests);
    }

    /// <summary>Seeded events reach the reactor with the metadata the test attached.</summary>
    [Fact]
    public async Task ShouldDeliverEventsUnchangedGivenSeededEvents()
    {
        // Arrange
        var eventId = Uuid.CreateVersion4();
        var scenario = new ReactorScenario()
            .Given(DomainEventSeed.Attach(new UserCreated(Uuid.CreateVersion4()), Uuid.CreateVersion4(), 1,
                eventId: eventId));
        var reactor = new WelcomeReactor(scenario.Requests);

        // Act
        await scenario.RunAsync(reactor, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(eventId, reactor.LastEventId);
    }
}
