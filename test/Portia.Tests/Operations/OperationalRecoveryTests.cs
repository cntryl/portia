using System.Security.Claims;

namespace Cntryl.Portia;

/// <summary>Exercises the durable approval and worker-owned recovery boundary.</summary>
public sealed class OperationalRecoveryTests
{
    static readonly OperationalTarget Target = new("reactor", "stream://operations/orders/*", OperationalScope.Global);

    static readonly OperationalProcessingSettings Settings = new(512, 4096, 3, TimeSpan.TicksPerSecond,
        TimeSpan.TicksPerMinute);

    /// <summary>A resource-lock conflict rebuilds the plan from the winning writer's durable state.</summary>
    [Fact]
    public async Task ShouldRebuildRepairPlanGivenConcurrentOperationalWrite()
    {
        // Arrange
        var store = new MemoryOperationalStore();
        var session = await BlockAsync(store);
        var current = session.State;

        // Act
        var revisedSettings = Settings with { MaxBatchSize = 128 };

        // Assert
        store.BeforeExchange = async () =>
        {
            store.BeforeExchange = null;
            Assert.True(await store.CompareExchangeAsync(current with
            {
                Revision = current.Revision + 1,
                Settings = revisedSettings
            }, current.Revision, current.OwnerSession));
            throw new ProjectionConcurrencyException("A concurrent worker holds the resource lock.");
        };

        var plan = await Api(store).PreviewRepairAsync(TestOperationalAuthorizer.AdministratorActor(), Target, TestContext.Current.CancellationToken);

        Assert.Equal(revisedSettings, plan.Settings);
        var saved = (await store.ReadAsync(Target, TestContext.Current.CancellationToken))!;
        Assert.Equal(current.Revision + 2, saved.Revision);
        Assert.Equal(plan.Id, Assert.Single(saved.Plans).Id);
        Assert.Equal(current.OwnerSession, saved.OwnerSession);
        Assert.Equal(current.Block, saved.Block);
    }

    /// <summary>Cancellation ends lock-conflict recovery without persisting a plan.</summary>
    [Fact]
    public async Task ShouldStopOperationalConflictRecoveryGivenCancellation()
    {
        // Arrange
        var store = new MemoryOperationalStore();
        _ = await BlockAsync(store);
        using var cancellation = new CancellationTokenSource();

        // Act
        store.BeforeExchange = () =>
        {
            cancellation.Cancel();
            throw new ProjectionConcurrencyException("A concurrent worker holds the resource lock.");
        };

        // Assert
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Api(store)
            .PreviewRepairAsync(TestOperationalAuthorizer.AdministratorActor(), Target, cancellation.Token).AsTask());

        Assert.Empty((await store.ReadAsync(Target, TestContext.Current.CancellationToken))!.Plans);
    }

    /// <summary>Operations never grants access without a host policy.</summary>
    [Fact]
    public async Task ShouldDenyAccessGivenUnconfiguredOperationalAuthorizer()
    {
        // Arrange
        var operations = new PortiaOperations(new MemoryOperationalStore(), new OperationalOptions(),
            new EmptyApplicationTopology());

        // Act
        var exception = await Record.ExceptionAsync(async () =>
            await operations.ListIssuesAsync(new ClaimsPrincipal(), OperationalScope.Global, TestContext.Current.CancellationToken));

        // Assert
        _ = Assert.IsType<UnauthorizedAccessException>(exception);
    }

    /// <summary>Operator approval is exact and duplicates enqueue only one execution.</summary>
    [Fact]
    public async Task ShouldRequireSeparateApprovalAndQueueOnceGivenBlockedProcessor()
    {
        // Arrange
        var store = new MemoryOperationalStore();
        var session = await BlockAsync(store);
        var operations = Api(store);
        var agent = TestOperationalAuthorizer.AdministratorActor();

        // Act
        var plan = await operations.PreviewRepairAsync(agent, Target, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(session.State.OwnerSession, plan.OwnerSession);
        _ = await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await operations.ExecuteRepairAsync(agent, Target, plan.Id, TestContext.Current.CancellationToken));
        _ = await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await operations.ApproveRepairAsync(agent, Target, plan.Id, TestContext.Current.CancellationToken));
        _ = await operations.ApproveRepairAsync(TestOperationalAuthorizer.AdministratorActor(true), Target, plan.Id, TestContext.Current.CancellationToken);
        var first = await operations.ExecuteRepairAsync(agent, Target, plan.Id, TestContext.Current.CancellationToken);
        Assert.Equal(first, await operations.ExecuteRepairAsync(agent, Target, plan.Id, TestContext.Current.CancellationToken));
        Assert.Equal("queued", first.Status);
        Assert.Single((await store.ReadAsync(Target, TestContext.Current.CancellationToken))!.Repairs);
        Assert.True(await session.PrepareAsync(ProjectionCheckpoint.Start, TestContext.Current.CancellationToken));
        Assert.Equal("running", (await operations.VerifyRepairAsync(agent, Target, plan.Id, TestContext.Current.CancellationToken)).Status);
    }

    /// <summary>A resume without committed source evidence stays open until the new pass proves recovery or fails.</summary>
    [Theory]
    [InlineData(true, "resumed")]
    [InlineData(false, "failed")]
    public async Task ShouldReportTheNextPassOutcomeGivenResumeAfterSourceReadFailure(
        bool sourceRecovers,
        string expectedStatus
    )
    {
        // Arrange
        var store = new MemoryOperationalStore();
        var session = await BlockAsync(store);
        var operations = Api(store);
        var actor = TestOperationalAuthorizer.AdministratorActor(true);
        var plan = await operations.PreviewRepairAsync(actor, Target, TestContext.Current.CancellationToken);
        _ = await operations.ApproveRepairAsync(actor, Target, plan.Id, TestContext.Current.CancellationToken);
        _ = await operations.ExecuteRepairAsync(actor, Target, plan.Id, TestContext.Current.CancellationToken);
        var checkpoint = ProjectionCheckpoint.Start;
        var failure = new ProcessorFailureException(Target.Identity, checkpoint, ProcessorFailureStage.Reading,
            error: RequestError.Validation("source unavailable"));

        // Act
        var mayResume = await session.PrepareAsync(checkpoint, TestContext.Current.CancellationToken);
        var claimed = await operations.VerifyRepairAsync(actor, Target, plan.Id, TestContext.Current.CancellationToken);

        if (sourceRecovers)
        {
            session.EnsureRecoveryCompleted(checkpoint);
            await session.PassSucceededAsync(TestContext.Current.CancellationToken);
        }
        else
            await session.RecordFailureAsync(failure, null, TestContext.Current.CancellationToken);

        var settled = await operations.VerifyRepairAsync(actor, Target, plan.Id, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(mayResume);
        Assert.Equal("running", claimed.Status);
        Assert.Equal(expectedStatus, settled.Status);
        Assert.Equal(checkpoint.Cursor.Value, session.State.Checkpoint);
        Assert.Equal(sourceRecovers, session.State.Block is null);
    }

    /// <summary>A claimed resume without processing evidence cannot unblock a replacement owner.</summary>
    [Fact]
    public async Task ShouldPreserveTheBlockGivenOwnershipChangesBeforeResumeVerifiesTheSource()
    {
        // Arrange
        var store = new MemoryOperationalStore();
        var owner = await BlockAsync(store);
        var operations = Api(store);
        var actor = TestOperationalAuthorizer.AdministratorActor(true);
        var plan = await operations.PreviewRepairAsync(actor, Target, TestContext.Current.CancellationToken);
        _ = await operations.ApproveRepairAsync(actor, Target, plan.Id, TestContext.Current.CancellationToken);
        _ = await operations.ExecuteRepairAsync(actor, Target, plan.Id, TestContext.Current.CancellationToken);
        var checkpoint = ProjectionCheckpoint.Start;
        _ = await owner.PrepareAsync(checkpoint, TestContext.Current.CancellationToken);
        var next = new OperationalSession(store, new OperationalOptions(), new EmptyApplicationTopology(),
            TimeProvider.System);

        // Act
        await next.AcquireAsync(Target, "reactor", Settings, checkpoint, TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(owner.State.Block);
        Assert.Equal(owner.State.Block, next.State.Block);
        Assert.Equal("indeterminate", (await operations.VerifyRepairAsync(actor, Target, plan.Id, TestContext.Current.CancellationToken)).Status);
    }

    /// <summary>Ownership handoff preserves blocks but invalidates old plans and writes.</summary>
    [Fact]
    public async Task ShouldFenceOldOwnersAndInvalidatePlansGivenNewSessionAcquiringProcessor()
    {
        // Arrange
        var store = new MemoryOperationalStore();
        var old = await BlockAsync(store);
        var stale = old.State;
        var operations = Api(store);
        var actor = TestOperationalAuthorizer.AdministratorActor(true);
        var plan = await operations.PreviewRepairAsync(actor, Target, TestContext.Current.CancellationToken);
        var next = new OperationalSession(store, new OperationalOptions(), new EmptyApplicationTopology(),
            TimeProvider.System);

        // Act
        await next.AcquireAsync(Target, "reactor", Settings, ProjectionCheckpoint.Start, TestContext.Current.CancellationToken);

        // Assert
        Assert.NotEqual(stale.OwnerSession, next.State.OwnerSession);
        Assert.NotNull(next.State.Block);
        Assert.False(await store.CompareExchangeAsync(stale with { Revision = stale.Revision + 1 }, stale.Revision,
            stale.OwnerSession, TestContext.Current.CancellationToken));
        _ = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await operations.ApproveRepairAsync(actor, Target, plan.Id, TestContext.Current.CancellationToken));
    }

    /// <summary>Every read and action has an exact authorized scope.</summary>
    [Fact]
    public async Task ShouldIsolateTenantEvidenceGivenTenantScopedActor()
    {
        // Arrange
        var store = new MemoryOperationalStore();
        _ = await BlockAsync(store);

        // Act
        var operations = Api(store);

        // Assert
        _ = await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await operations.InspectProcessorAsync(TestOperationalAuthorizer.AdministratorActor(scope: "tenant-a"),
                Target, TestContext.Current.CancellationToken));
        Assert.Empty(await operations.ListIssuesAsync(TestOperationalAuthorizer.AdministratorActor(scope: "tenant-a"),
            OperationalScope.ForTenant(new TenantId("tenant-a")), TestContext.Current.CancellationToken));
    }

    /// <summary>Expiry, revocation and opaque checkpoint changes all invalidate execution.</summary>
    [Theory]
    [InlineData("expired")]
    [InlineData("revoked")]
    [InlineData("checkpoint")]
    public async Task ShouldRejectApprovalGivenExpiredOrRevokedPlan(string change)
    {
        // Arrange
        var store = new MemoryOperationalStore();
        _ = await BlockAsync(store);
        var clock = new ManualTestClock();
        var operations = new PortiaOperations(TestOperationalAuthorizer.Configuration(), store,
            new OperationalOptions(), new EmptyApplicationTopology(),
            new TestOperationalAuthorizer(), clock);
        var actor = TestOperationalAuthorizer.AdministratorActor(true);
        var plan = await operations.PreviewRepairAsync(actor, Target, TestContext.Current.CancellationToken);
        _ = await operations.ApproveRepairAsync(actor, Target, plan.Id, TestContext.Current.CancellationToken);

        if (change == "expired")
            clock.Advance(TimeSpan.FromMinutes(5));

        // Act

        if (change == "revoked")
            await operations.RevokeRepairAsync(actor, Target, plan.Id, TestContext.Current.CancellationToken);

        // Assert
        if (change == "checkpoint")
        {
            var state = (await store.ReadAsync(Target, TestContext.Current.CancellationToken))!;
            Assert.True(await store.CompareExchangeAsync(state with
            {
                Revision = state.Revision + 1,
                Checkpoint = "opaque:changed"
            }, state.Revision, state.OwnerSession, TestContext.Current.CancellationToken));
        }


        if (change == "revoked")
            _ = await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
                await operations.ExecuteRepairAsync(actor, Target, plan.Id, TestContext.Current.CancellationToken));
        else
            _ = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await operations.ExecuteRepairAsync(actor, Target, plan.Id, TestContext.Current.CancellationToken));

        Assert.Empty((await store.ReadAsync(Target, TestContext.Current.CancellationToken))!.Repairs);
    }

    /// <summary>Approval revocation after queueing prevents the owning worker from invoking anything.</summary>
    [Fact]
    public async Task ShouldInvalidateRepairGivenApprovedPlanRevokedAfterExecution()
    {
        // Arrange
        var store = new MemoryOperationalStore();
        var session = await BlockAsync(store);
        var operations = Api(store);
        var actor = TestOperationalAuthorizer.AdministratorActor(true);
        var plan = await operations.PreviewRepairAsync(actor, Target, TestContext.Current.CancellationToken);
        _ = await operations.ApproveRepairAsync(actor, Target, plan.Id, TestContext.Current.CancellationToken);
        _ = await operations.ExecuteRepairAsync(actor, Target, plan.Id, TestContext.Current.CancellationToken);

        // Act
        await operations.RevokeRepairAsync(actor, Target, plan.Id, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(await session.PrepareAsync(ProjectionCheckpoint.Start, TestContext.Current.CancellationToken));
        Assert.Equal("invalidated", (await operations.VerifyRepairAsync(actor, Target, plan.Id, TestContext.Current.CancellationToken)).Status);
        Assert.Single(session.State.Attempts);
    }

    /// <summary>Completed recovery evidence expires while the current unresolved failure remains retained.</summary>
    [Fact]
    public async Task ShouldRetainUnresolvedFailuresGivenRecordedProcessorFailure()
    {
        // Arrange
        var store = new MemoryOperationalStore();
        var clock = new ManualTestClock();
        var session = new OperationalSession(store, new OperationalOptions(), new EmptyApplicationTopology(), clock);
        await session.AcquireAsync(Target, "reactor", Settings, ProjectionCheckpoint.Start, TestContext.Current.CancellationToken);
        var failure = new ProcessorFailureException(Target.Identity, ProjectionCheckpoint.Start,
            ProcessorFailureStage.Reading,
            error: RequestError.Validation("dependency unavailable"));

        // Act
        await session.RecordFailureAsync(failure, null, TestContext.Current.CancellationToken);

        // Assert
        var original = Assert.Single(session.State.Attempts).Id;
        var operations = new PortiaOperations(TestOperationalAuthorizer.Configuration(), store,
            new OperationalOptions(), new EmptyApplicationTopology(),
            new TestOperationalAuthorizer(), clock);
        var actor = TestOperationalAuthorizer.AdministratorActor(true);
        var plan = await operations.PreviewRepairAsync(actor, Target, TestContext.Current.CancellationToken);
        _ = await operations.ApproveRepairAsync(actor, Target, plan.Id, TestContext.Current.CancellationToken);
        _ = await operations.ExecuteRepairAsync(actor, Target, plan.Id, TestContext.Current.CancellationToken);
        Assert.True(await session.PrepareAsync(ProjectionCheckpoint.Start, TestContext.Current.CancellationToken));
        await session.PassSucceededAsync(TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromDays(31));
        session.BeginPass();
        await session.RecordFailureAsync(failure, null, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(session.State.Attempts, attempt => attempt.Id == original);
        Assert.Equal(DateTimeOffset.UnixEpoch + TimeSpan.FromDays(1), session.State.RetainedSince);
        Assert.Equal(Assert.Single(session.State.Attempts).Id, session.State.Block!.AttemptId);
    }

    static PortiaOperations Api(MemoryOperationalStore store) => new(TestOperationalAuthorizer.Configuration(), store,
        new OperationalOptions(),
        new EmptyApplicationTopology(), new TestOperationalAuthorizer(), null);

    static async Task<OperationalSession> BlockAsync(MemoryOperationalStore store)
    {
        var session = new OperationalSession(store, new OperationalOptions(), new EmptyApplicationTopology(),
            TimeProvider.System);
        await session.AcquireAsync(Target, "reactor", Settings, ProjectionCheckpoint.Start, default);
        await session.RecordFailureAsync(new ProcessorFailureException(Target.Identity, ProjectionCheckpoint.Start,
                ProcessorFailureStage.Reading, error: new RequestError(RequestErrorKind.Validation, "private message")),
            null, default);
        Assert.NotNull(session.State.Block);
        return session;
    }
}
