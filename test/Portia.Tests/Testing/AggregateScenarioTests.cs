namespace Cntryl.Portia;

/// <summary>
///     Verifies <see cref="AggregateScenario{TAggregate}" /> lets business tests read as given history, when an
///     operation, then the events it raised — with no hand-numbered metadata and no persistence wiring.
/// </summary>
public sealed class AggregateScenarioTests
{
    /// <summary>Bare events are replayed as history, numbered from the aggregate's current version.</summary>
    [Fact]
    public void ShouldReplayBareEventsAsHistory()
    {
        // Arrange
        var scenario = new AggregateScenario<TestAggregate>(new TestAggregate(Uuid.CreateVersion4()));

        // Act
        _ = scenario
            .Given(new ValueChanged(3), new ValueIncremented(4))
            .Given(new ValueIncremented(1));

        // Assert
        Assert.Equal(8, scenario.Aggregate.Value);
        Assert.Equal(3UL, scenario.Aggregate.Version);
        Assert.Equal(3UL, scenario.CommittedEventCount);
        Assert.Empty(scenario.PendingEvents);
    }

    /// <summary>Events that already carry metadata are replayed as seeded, so tests keep full control when they need it.</summary>
    [Fact]
    public void ShouldReplaySeededEventsUnchanged()
    {
        // Arrange
        var id = Uuid.CreateVersion4();
        var eventId = Uuid.CreateVersion4();
        var seeded = DomainEventSeed.Attach(new ValueChanged(3), id, 1, eventId: eventId);

        // Act
        var scenario = new AggregateScenario<TestAggregate>(new TestAggregate(id)).Given(seeded);

        // Assert
        Assert.Equal(3, scenario.Aggregate.Value);
        Assert.Equal(eventId, seeded.Metadata.EventId);
    }

    /// <summary>A committed operation leaves what it raised pending, comparable to expected payloads.</summary>
    [Fact]
    public void ShouldKeepRaisedEventsWhenOperationCommits()
    {
        // Arrange
        var scenario = new AggregateScenario<TestAggregate>(new TestAggregate(Uuid.CreateVersion4()))
            .Given(new ValueChanged(3));

        // Act
        var result = scenario.When(aggregate =>
        {
            aggregate.ChangeValue(5);
            return AggregateOutcome.Commit(Result.Success);
        });

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal([new ValueChanged(5)], scenario.PendingEvents);
    }

    /// <summary>A discarded operation leaves nothing pending, exactly as the executor would persist nothing.</summary>
    [Fact]
    public void ShouldDropRaisedEventsWhenOperationDiscards()
    {
        // Arrange
        var scenario = new AggregateScenario<TestAggregate>(new TestAggregate(Uuid.CreateVersion4()));

        // Act
        var result = scenario.When(aggregate =>
        {
            aggregate.ChangeValue(5);
            return AggregateOutcome.CommitOnSuccess(Result.Failure(new RequestError(RequestErrorKind.Conflict, "taken")));
        });

        // Assert
        Assert.Equal(RequestErrorKind.Conflict, result.Error!.Kind);
        Assert.Empty(scenario.PendingEvents);
    }

    /// <summary>A value-returning operation returns its result and keeps committed records.</summary>
    [Fact]
    public void ShouldReturnValueAndKeepAuditsWhenOperationCommits()
    {
        // Arrange
        var scenario = new AggregateScenario<TestAggregate>(new TestAggregate(Uuid.CreateVersion4()));

        // Act
        var result = scenario.When(aggregate =>
        {
            aggregate.Audit("checked");
            return AggregateOutcome.Commit(Result<int>.Success(aggregate.Value));
        });

        // Assert
        Assert.Equal(0, result.Value);
        Assert.Equal([new ValueAudited("checked")], scenario.PendingAudits);
    }

    /// <summary>
    ///     A committed operation is saved before the next one, as the executor would have saved it, so pending
    ///     records show only the latest operation and an audit followed by a raise behaves as in production.
    /// </summary>
    [Fact]
    public void ShouldSaveCommittedOperationBeforeNextOperation()
    {
        // Arrange
        var scenario = new AggregateScenario<TestAggregate>(new TestAggregate(Uuid.CreateVersion4()));
        _ = scenario.When(aggregate =>
        {
            aggregate.Audit("checked");
            return AggregateOutcome.Commit(Result.Success);
        });

        // Act
        _ = scenario.When(aggregate =>
        {
            aggregate.ChangeValue(5);
            return AggregateOutcome.Commit(Result.Success);
        });

        // Assert
        Assert.Empty(scenario.PendingAudits);
        Assert.Equal([new ValueChanged(5)], scenario.PendingEvents);
        Assert.Equal(0UL, scenario.CommittedEventCount);
    }

    /// <summary>History can follow a committed operation, since that operation was saved.</summary>
    [Fact]
    public void ShouldAcceptHistoryAfterCommittedOperation()
    {
        // Arrange
        var scenario = new AggregateScenario<TestAggregate>(new TestAggregate(Uuid.CreateVersion4()));
        _ = scenario.When(aggregate =>
        {
            aggregate.ChangeValue(5);
            return AggregateOutcome.Commit(Result.Success);
        });

        // Act
        _ = scenario.Given(new ValueIncremented(2));

        // Assert
        Assert.Equal(7, scenario.Aggregate.Value);
        Assert.Equal(2UL, scenario.CommittedEventCount);
    }

    /// <summary>
    ///     An operation that throws after raising leaves nothing to save, and the instance refuses further
    ///     operations exactly as it would after the executor discarded it.
    /// </summary>
    [Fact]
    public void ShouldDiscardWhatAThrowingOperationRaised()
    {
        // Arrange
        var scenario = new AggregateScenario<TestAggregate>(new TestAggregate(Uuid.CreateVersion4()));
        var throwing = Record.Exception(() => scenario.When(aggregate =>
        {
            aggregate.ChangeValue(5);
            throw new DivideByZeroException();
        }));
        var pendingAfterThrow = scenario.PendingEvents.ToList();

        // Act
        var further = Record.Exception(() => scenario.When(aggregate =>
        {
            aggregate.ChangeValue(6);
            return AggregateOutcome.Commit(Result.Success);
        }));

        // Assert
        _ = Assert.IsType<DivideByZeroException>(throwing);
        Assert.Empty(pendingAfterThrow);
        _ = Assert.IsType<InvalidOperationException>(further);
    }

    /// <summary>What a direct call on the aggregate raised is saved before the next history, like a committed When.</summary>
    [Fact]
    public void ShouldSaveDirectCallsBeforeFurtherHistory()
    {
        // Arrange
        var scenario = new AggregateScenario<TestAggregate>(new TestAggregate(Uuid.CreateVersion4()));
        scenario.Aggregate.ChangeValue(5);

        // Act
        _ = scenario.Given(new ValueIncremented(2));

        // Assert
        Assert.Equal(7, scenario.Aggregate.Value);
        Assert.Equal(2UL, scenario.CommittedEventCount);
    }

    /// <summary>An uninitialized outcome is a bug in the operation, reported the same way the executor reports it.</summary>
    [Fact]
    public void ShouldThrowWhenOperationReturnsUninitializedOutcome()
    {
        // Arrange
        var scenario = new AggregateScenario<TestAggregate>(new TestAggregate(Uuid.CreateVersion4()));

        // Act
        var exception = Record.Exception(() => scenario.When(_ => default(AggregateOutcome)));

        // Assert
        _ = Assert.IsType<InvalidOperationException>(exception);
    }

    /// <summary>The shortcut commits a successful result and retains its raised event.</summary>
    [Fact]
    public void ShouldCommitSuccessfulResultThroughShortcut()
    {
        // Arrange
        var scenario = new AggregateScenario<TestAggregate>(new TestAggregate(Uuid.CreateVersion4()));

        // Act
        var result = scenario.WhenCommitOnSuccess(aggregate =>
        {
            aggregate.ChangeValue(5);
            return Result.Success;
        });

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal([new ValueChanged(5)], scenario.PendingEvents);
    }

    /// <summary>The shortcut discards a failed value result and prevents later operations.</summary>
    [Fact]
    public void ShouldDiscardFailedValueResultThroughShortcut()
    {
        // Arrange
        var scenario = new AggregateScenario<TestAggregate>(new TestAggregate(Uuid.CreateVersion4()));

        // Act
        var result = scenario.WhenCommitOnSuccess(aggregate =>
        {
            aggregate.ChangeValue(5);
            return Result<int>.Failure(new RequestError(RequestErrorKind.Conflict, "taken"));
        });

        // Assert
        Assert.Equal(RequestErrorKind.Conflict, result.Error!.Kind);
        Assert.Empty(scenario.PendingEvents);
        _ = Assert.Throws<InvalidOperationException>(() => scenario.WhenCommitOnSuccess(aggregate =>
        {
            aggregate.ChangeValue(6);
            return Result.Success;
        }));
    }

    /// <summary>A successful value result is returned and its audit is retained.</summary>
    [Fact]
    public void ShouldReturnSuccessfulValueThroughShortcut()
    {
        // Arrange
        var scenario = new AggregateScenario<TestAggregate>(new TestAggregate(Uuid.CreateVersion4()));

        // Act
        var result = scenario.WhenCommitOnSuccess(aggregate =>
        {
            aggregate.Audit("checked");
            return Result<int>.Success(aggregate.Value);
        });

        // Assert
        Assert.Equal(0, result.Value);
        Assert.Equal([new ValueAudited("checked")], scenario.PendingAudits);
    }
}
