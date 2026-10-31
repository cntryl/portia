using System.Security.Claims;

namespace Cntryl.Portia;

/// <summary>Exercises the durable approval and worker-owned recovery boundary.</summary>
public sealed class OperationalRecoveryTests
{
    static readonly OperationalTarget Target = new("reactor", "stream://operations/orders/*", OperationalScope.Global);
    static readonly OperationalProcessingSettings Settings = new(512, 4096, 3, TimeSpan.TicksPerSecond, TimeSpan.TicksPerMinute);

    /// <summary>A resource-lock conflict rebuilds the plan from the winning writer's durable state.</summary>
    [Fact]
    public async Task ShouldRebuildRepairPlanGivenConcurrentOperationalWrite()
    {
        var store = new MemoryOperationalStore();
        var session = await BlockAsync(store);
        var current = session.State;
        var revisedSettings = Settings with { MaxBatchSize = 128 };
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

        var plan = await Api(store).PreviewRepairAsync(TestOperationalAuthorizer.Actor(), Target);

        Assert.Equal(revisedSettings, plan.Settings);
        var saved = (await store.ReadAsync(Target))!;
        Assert.Equal(current.Revision + 2, saved.Revision);
        Assert.Equal(plan.Id, Assert.Single(saved.Plans).Id);
        Assert.Equal(current.OwnerSession, saved.OwnerSession);
        Assert.Equal(current.Block, saved.Block);
    }

    /// <summary>Cancellation ends lock-conflict recovery without persisting a plan.</summary>
    [Fact]
    public async Task ShouldStopOperationalConflictRecoveryGivenCancellation()
    {
        var store = new MemoryOperationalStore();
        _ = await BlockAsync(store);
        using var cancellation = new CancellationTokenSource();
        store.BeforeExchange = () =>
        {
            cancellation.Cancel();
            throw new ProjectionConcurrencyException("A concurrent worker holds the resource lock.");
        };

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Api(store)
            .PreviewRepairAsync(TestOperationalAuthorizer.Actor(), Target, cancellation.Token).AsTask());

        Assert.Empty((await store.ReadAsync(Target))!.Plans);
    }

    /// <summary>Operations never grants access without a host policy.</summary>
    [Fact]
    public async Task ShouldDenyUnconfiguredOperationalAccess()
    {
        var operations = new PortiaOperations(new MemoryOperationalStore(), new OperationalOptions(),
            new EmptyApplicationTopology());
        _ = await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await operations.ListIssuesAsync(new ClaimsPrincipal(), OperationalScope.Global));
    }

    /// <summary>Operator approval is exact and duplicates enqueue only one execution.</summary>
    [Fact]
    public async Task ShouldRequireSeparateApprovalAndQueueOnlyOnce()
    {
        var store = new MemoryOperationalStore();
        var session = await BlockAsync(store);
        var operations = Api(store);
        var agent = TestOperationalAuthorizer.Actor();
        var plan = await operations.PreviewRepairAsync(agent, Target);
        Assert.Equal(session.State.OwnerSession, plan.OwnerSession);
        _ = await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await operations.ExecuteRepairAsync(agent, Target, plan.Id));
        _ = await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await operations.ApproveRepairAsync(agent, Target, plan.Id));
        _ = await operations.ApproveRepairAsync(TestOperationalAuthorizer.Actor(true), Target, plan.Id);
        var first = await operations.ExecuteRepairAsync(agent, Target, plan.Id);
        Assert.Equal(first, await operations.ExecuteRepairAsync(agent, Target, plan.Id));
        Assert.Equal("queued", first.Status);
        Assert.Single((await store.ReadAsync(Target))!.Repairs);
        Assert.True(await session.PrepareAsync(ProjectionCheckpoint.Start, default));
        Assert.Equal("committed", (await operations.VerifyRepairAsync(agent, Target, plan.Id)).Status);
    }

    /// <summary>Ownership handoff preserves blocks but invalidates old plans and writes.</summary>
    [Fact]
    public async Task ShouldFenceOldOwnersAndInvalidatePlans()
    {
        var store = new MemoryOperationalStore();
        var old = await BlockAsync(store);
        var stale = old.State;
        var operations = Api(store);
        var actor = TestOperationalAuthorizer.Actor(true);
        var plan = await operations.PreviewRepairAsync(actor, Target);
        var next = new OperationalSession(store, new OperationalOptions(), new EmptyApplicationTopology(), TimeProvider.System);
        await next.AcquireAsync(Target, "reactor", Settings, ProjectionCheckpoint.Start, default);
        Assert.NotEqual(stale.OwnerSession, next.State.OwnerSession);
        Assert.NotNull(next.State.Block);
        Assert.False(await store.CompareExchangeAsync(stale with { Revision = stale.Revision + 1 }, stale.Revision, stale.OwnerSession));
        _ = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await operations.ApproveRepairAsync(actor, Target, plan.Id));
    }

    /// <summary>Every read and action has an exact authorized scope.</summary>
    [Fact]
    public async Task ShouldIsolateTenantEvidence()
    {
        var store = new MemoryOperationalStore();
        _ = await BlockAsync(store);
        var operations = Api(store);
        _ = await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await operations.InspectProcessorAsync(TestOperationalAuthorizer.Actor(scope: "tenant-a"), Target));
        Assert.Empty(await operations.ListIssuesAsync(TestOperationalAuthorizer.Actor(scope: "tenant-a"),
            OperationalScope.ForTenant(new TenantId("tenant-a"))));
    }

    /// <summary>Expiry, revocation and opaque checkpoint changes all invalidate execution.</summary>
    [Theory]
    [InlineData("expired")]
    [InlineData("revoked")]
    [InlineData("checkpoint")]
    public async Task ShouldRejectInvalidatedApproval(string change)
    {
        var store = new MemoryOperationalStore();
        _ = await BlockAsync(store);
        var clock = new ManualTestClock();
        var operations = new PortiaOperations(store, new OperationalOptions(), new EmptyApplicationTopology(),
            new TestOperationalAuthorizer(), clock);
        var actor = TestOperationalAuthorizer.Actor(true);
        var plan = await operations.PreviewRepairAsync(actor, Target);
        _ = await operations.ApproveRepairAsync(actor, Target, plan.Id);
        if (change == "expired")
            clock.Advance(TimeSpan.FromMinutes(5));
        if (change == "revoked")
            await operations.RevokeRepairAsync(actor, Target, plan.Id);
        if (change == "checkpoint")
        {
            var state = (await store.ReadAsync(Target))!;
            Assert.True(await store.CompareExchangeAsync(state with
            {
                Revision = state.Revision + 1,
                Checkpoint = "opaque:changed"
            }, state.Revision, state.OwnerSession));
        }
        if (change == "revoked")
            _ = await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await operations.ExecuteRepairAsync(actor, Target, plan.Id));
        else
            _ = await Assert.ThrowsAsync<InvalidOperationException>(async () => await operations.ExecuteRepairAsync(actor, Target, plan.Id));
        Assert.Empty((await store.ReadAsync(Target))!.Repairs);
    }

    /// <summary>Approval revocation after queueing prevents the owning worker from invoking anything.</summary>
    [Fact]
    public async Task ShouldRecheckRevocationBeforeClaim()
    {
        var store = new MemoryOperationalStore();
        var session = await BlockAsync(store);
        var operations = Api(store);
        var actor = TestOperationalAuthorizer.Actor(true);
        var plan = await operations.PreviewRepairAsync(actor, Target);
        _ = await operations.ApproveRepairAsync(actor, Target, plan.Id);
        _ = await operations.ExecuteRepairAsync(actor, Target, plan.Id);
        await operations.RevokeRepairAsync(actor, Target, plan.Id);
        Assert.False(await session.PrepareAsync(ProjectionCheckpoint.Start, default));
        Assert.Equal("invalidated", (await operations.VerifyRepairAsync(actor, Target, plan.Id)).Status);
        Assert.Single(session.State.Attempts);
    }

    /// <summary>Completed recovery evidence expires while the current unresolved failure remains retained.</summary>
    [Fact]
    public async Task ShouldRetainUnresolvedFailuresAndExposeCompletedHistoryBoundary()
    {
        var store = new MemoryOperationalStore();
        var clock = new ManualTestClock();
        var session = new OperationalSession(store, new OperationalOptions(), new EmptyApplicationTopology(), clock);
        await session.AcquireAsync(Target, "reactor", Settings, ProjectionCheckpoint.Start, default);
        var failure = new ProcessorFailureException(Target.Identity, ProjectionCheckpoint.Start, ProcessorFailureStage.Reading,
            error: RequestError.Validation("dependency unavailable"));
        await session.RecordFailureAsync(failure, null, default);
        var original = Assert.Single(session.State.Attempts).Id;
        var operations = new PortiaOperations(store, new OperationalOptions(), new EmptyApplicationTopology(),
            new TestOperationalAuthorizer(), clock);
        var actor = TestOperationalAuthorizer.Actor(true);
        var plan = await operations.PreviewRepairAsync(actor, Target);
        _ = await operations.ApproveRepairAsync(actor, Target, plan.Id);
        _ = await operations.ExecuteRepairAsync(actor, Target, plan.Id);
        Assert.True(await session.PrepareAsync(ProjectionCheckpoint.Start, default));
        clock.Advance(TimeSpan.FromDays(31));
        session.BeginPass();
        await session.RecordFailureAsync(failure, null, default);
        Assert.DoesNotContain(session.State.Attempts, attempt => attempt.Id == original);
        Assert.Equal(DateTimeOffset.UnixEpoch + TimeSpan.FromDays(1), session.State.RetainedSince);
        Assert.Equal(Assert.Single(session.State.Attempts).Id, session.State.Block!.AttemptId);
    }

    static PortiaOperations Api(MemoryOperationalStore store) => new(store, new OperationalOptions(),
        new EmptyApplicationTopology(), new TestOperationalAuthorizer());

    static async Task<OperationalSession> BlockAsync(MemoryOperationalStore store)
    {
        var session = new OperationalSession(store, new OperationalOptions(), new EmptyApplicationTopology(), TimeProvider.System);
        await session.AcquireAsync(Target, "reactor", Settings, ProjectionCheckpoint.Start, default);
        await session.RecordFailureAsync(new ProcessorFailureException(Target.Identity, ProjectionCheckpoint.Start,
            ProcessorFailureStage.Reading, error: new RequestError(RequestErrorKind.Validation, "private message")), null, default);
        Assert.NotNull(session.State.Block);
        return session;
    }
}
