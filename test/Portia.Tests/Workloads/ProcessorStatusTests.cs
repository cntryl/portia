using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Cntryl.Portia;

/// <summary>Verifies checkpoint-derived processor inspection, scoped authorization, and partial observations.</summary>
public sealed class ProcessorStatusTests
{
    const string AdministratorRole = "platform-operators";

    /// <summary>Reads committed progress without beginning a projection or executing a reaction.</summary>
    /// <param name="reactor">Whether to inspect a reactor instead of a projector.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldReadAuthoritativeProgressGivenProjectorOrReactorNotRunning(bool reactor)
    {
        // Arrange
        var checkpoint = new StatusCheckpointStore();
        var identity = new CheckpointIdentity("registered-name", EventStreamPattern.ForPattern("global", "events"));
        checkpoint.Set(identity, "opaque-committed");
        var progress = new StatusProgressReader();
        var services = Services(checkpoint);
        _ = services.AddSingleton<IDomainEventProgressReader>(progress);
        var builder = services.AddPortia().RequireAuthorization();
        if (reactor)
            _ = builder.AddReactor<GlobalStatusReactor>(identity.ComponentName, WorkloadScope.Global);
        else
            _ = builder.AddProjector<GlobalStatusProjector>(identity.ComponentName, WorkloadScope.Global);
        await using var provider = services.BuildServiceProvider();

        // Act
        var result = await SendAsync(provider, new GetProcessorStatus(identity.ComponentName));

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(reactor ? "reactor" : "projector", result.Value.Kind);
        Assert.Equal(identity.ComponentName, result.Value.Processor);
        Assert.Equal(identity.Pattern.ToString(), result.Value.Pattern);
        Assert.Equal("opaque-committed", result.Value.Progress.Position.Value);
        Assert.Equal("opaque-head", result.Value.Progress.SourcePosition?.Value);
        Assert.Equal(3UL, result.Value.Progress.PendingEvents);
        Assert.False(result.Value.Progress.IsCaughtUp);
        Assert.Equal(identity, Assert.Single(checkpoint.Reads));
        Assert.Equal(identity.Pattern, progress.Pattern);
        Assert.Equal("opaque-committed", progress.Position.Value);
        Assert.Equal(0, checkpoint.Writes);
        Assert.Empty(provider.GetRequiredService<RequestRegistry>().UnprotectedRequestTypes);
    }

    /// <summary>Reads only the requested tenant and configured rebuild generation.</summary>
    [Fact]
    public async Task ShouldBindExactTenantAndRebuildGenerationGivenTenantScopedTarget()
    {
        // Arrange
        var store = new StatusCheckpointStore();
        var pattern = EventStreamPattern.ForPattern("tenant-a", "events");
        var identity = new CheckpointIdentity("tenant-projector", pattern, "candidate");
        store.Set(identity, "tenant-a-candidate");
        store.Set(new CheckpointIdentity(identity.ComponentName, pattern), "live");
        store.Set(new CheckpointIdentity(identity.ComponentName,
            EventStreamPattern.ForPattern("tenant-b", "events"), "candidate"), "tenant-b-candidate");
        var services = Services(store);
        _ = services.AddSingleton<ITenantDirectory>(new FixedTenantDirectory(new TenantId("tenant-a")));
        _ = services.AddPortia().AddProjector<TenantStatusProjector>(identity.ComponentName, WorkloadScope.PerTenant,
            options => options.Processing = new ProjectionRunOptions { RebuildId = "candidate" });
        await using var provider = services.BuildServiceProvider();

        // Act
        var result = await SendAsync(provider, new GetProcessorStatus(identity.ComponentName, "tenant-a"), "tenant-a");

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal("tenant-a", result.Value.Tenant);
        Assert.Equal("candidate", result.Value.RebuildId);
        Assert.Equal(pattern.ToString(), result.Value.Pattern);
        Assert.Equal("tenant-a-candidate", result.Value.Progress.Position.Value);
        Assert.Null(result.Value.Progress.PendingEvents);
        Assert.Null(result.Value.Progress.IsCaughtUp);
        Assert.Equal(identity, Assert.Single(store.Reads));
        Assert.Equal(0, store.Writes);
    }

    /// <summary>Allows configured administrators without requiring an application-owned operational policy.</summary>
    /// <param name="reactor">Whether to inspect a reactor instead of a projector.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldAllowAdministratorGivenConfiguredRoleAndNoOperationalPolicy(bool reactor)
    {
        // Arrange
        var store = new StatusCheckpointStore();
        var identity = new CheckpointIdentity("component", EventStreamPattern.ForPattern("global", "events"));
        store.Set(identity, "native-progress");
        var services = new ServiceCollection();
        _ = services.AddSingleton(store);
        _ = services.AddSingleton<IProjectionCheckpointStore>(store);
        _ = services.AddSingleton<IConfiguration>(Configuration());
        _ = services.AddSingleton<IDomainEventProgressReader>(new StatusProgressReader());
        var builder = services.AddPortia().RequireAuthorization();
        if (reactor)
            _ = builder.AddReactor<GlobalStatusReactor>(identity.ComponentName, WorkloadScope.Global);
        else
            _ = builder.AddProjector<GlobalStatusProjector>(identity.ComponentName, WorkloadScope.Global);

        // Act
        await using var provider = services.BuildServiceProvider();

        // Assert
        Assert.Null(provider.GetService<IOperationalAuthorizer>());

        var result = await SendAsync(provider, new GetProcessorStatus(identity.ComponentName));

        Assert.True(result.IsSuccess);
        Assert.Equal("native-progress", result.Value.Progress.Position.Value);
        Assert.Equal(3UL, result.Value.Progress.PendingEvents);
        Assert.Equal(identity, Assert.Single(store.Reads));
        Assert.Equal(0, store.Writes);
    }

    /// <summary>Accepts administrator roles on an authenticated secondary identity using the shared operator guard.</summary>
    /// <param name="roleClaimType">The role claim type selected by the host's authentication scheme.</param>
    [Theory]
    [InlineData(ClaimTypes.Role)]
    [InlineData("roles")]
    public async Task ShouldAcceptAdministratorGivenAuthenticatedSecondaryIdentity(string roleClaimType)
    {
        // Arrange
        var store = new StatusCheckpointStore();
        var services = Services(store);
        _ = services.AddPortia().AddProjector<GlobalStatusProjector>("projection", WorkloadScope.Global);
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var authenticated = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "operator"), new Claim("scope", "global"),
                new Claim(roleClaimType, AdministratorRole)], "test", ClaimTypes.Name, roleClaimType);
        var actor = new ClaimsPrincipal([new ClaimsIdentity(), authenticated]);

        // Act
        var result = await scope.ServiceProvider.GetRequiredService<IRequestBus>()
            .SendAsync(new GetProcessorStatus("projection"), actor);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Single(store.Reads);
    }

    /// <summary>A separate unauthenticated identity cannot supply the administrator role for inspection.</summary>
    /// <param name="roleClaimType">The role claim type selected by the host's authentication scheme.</param>
    [Theory]
    [InlineData(ClaimTypes.Role)]
    [InlineData("roles")]
    public async Task ShouldDenyAccessGivenUnauthenticatedAdministratorRoleOnMixedPrincipal(string roleClaimType)
    {
        // Arrange
        var store = new StatusCheckpointStore();
        var progress = new StatusProgressReader();
        var services = Services(store);
        _ = services.AddSingleton<IDomainEventProgressReader>(progress);
        _ = services.AddPortia().AddProjector<GlobalStatusProjector>("projection", WorkloadScope.Global);
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var ordinary = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "application-user"), new Claim("scope", "global")],
            "test", ClaimTypes.Name, roleClaimType);
        var unauthenticatedAdministrator = new ClaimsIdentity([new Claim(roleClaimType, AdministratorRole)],
            null, ClaimTypes.Name, roleClaimType);

        // Act
        var actor = new ClaimsPrincipal([ordinary, unauthenticatedAdministrator]);

        // Assert
        Assert.True(actor.IsInRole(AdministratorRole));
        Assert.True(await provider.GetRequiredService<IOperationalAuthorizer>()
            .AuthorizeAsync(actor, OperationalScope.Global, OperationalAccess.Read));

        var result = await scope.ServiceProvider.GetRequiredService<IRequestBus>()
            .SendAsync(new GetProcessorStatus("projection"), actor);

        Assert.False(result.IsSuccess);
        Assert.Equal(RequestErrorKind.Forbidden, result.Error.Kind);
        Assert.Empty(store.Reads);
        Assert.Null(progress.Pattern);
    }

    /// <summary>Refuses unauthenticated actors and honors an optional operational policy's scope restriction.</summary>
    /// <param name="actorScope">The actor's scope, or null for an unauthenticated actor.</param>
    /// <param name="error">The expected refusal classification.</param>
    [Theory]
    [InlineData("different-scope", RequestErrorKind.Forbidden)]
    [InlineData(null, RequestErrorKind.Unauthorized)]
    public async Task ShouldDenyInspectionGivenUnauthenticatedOrOutOfScopeCaller(string? actorScope,
        RequestErrorKind error)
    {
        // Arrange
        var store = new StatusCheckpointStore();
        var services = new ServiceCollection();
        _ = services.AddSingleton(store);
        _ = services.AddSingleton<IConfiguration>(Configuration());
        _ = services.AddSingleton<IOperationalAuthorizer, TestOperationalAuthorizer>();
        _ = services.AddPortia().AddProjector<GlobalStatusProjector>("projection", WorkloadScope.Global);
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var actor = actorScope is null ? RequestActor.Anonymous : SystemAdminActor(actorScope);

        // Act
        var result = await scope.ServiceProvider.GetRequiredService<IRequestBus>()
            .SendAsync(new GetProcessorStatus("projection"), actor);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(error, result.Error.Kind);
        Assert.Empty(store.Reads);
    }

    /// <summary>Requires the configured administrator role even when operational read policy permits the actor's exact scope.</summary>
    /// <param name="reactor">Whether the requested component is a reactor instead of a projector.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldDenyBeforeReadingProgressGivenOrdinaryUser(bool reactor)
    {
        // Arrange
        var store = new StatusCheckpointStore();
        var progress = new StatusProgressReader();
        var services = Services(store);
        _ = services.AddSingleton<IDomainEventProgressReader>(progress);
        var builder = services.AddPortia().RequireAuthorization();
        if (reactor)
            _ = builder.AddReactor<GlobalStatusReactor>("component", WorkloadScope.Global);
        else
            _ = builder.AddProjector<GlobalStatusProjector>("component", WorkloadScope.Global);
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        // Act
        var actor = TestOperationalAuthorizer.Actor();

        // Assert
        Assert.True(await provider.GetRequiredService<IOperationalAuthorizer>()
            .AuthorizeAsync(actor, OperationalScope.Global, OperationalAccess.Read));

        var result = await scope.ServiceProvider.GetRequiredService<IRequestBus>()
            .SendAsync(new GetProcessorStatus("component"), actor);

        Assert.False(result.IsSuccess);
        Assert.Equal(RequestErrorKind.Forbidden, result.Error.Kind);
        Assert.Empty(store.Reads);
        Assert.Null(progress.Pattern);
    }

    /// <summary>Rejects missing, blank, and unheld administrator roles before reading any processor state.</summary>
    /// <param name="configuredRole">The configured role, or null when the configuration has no role entry.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("other-platform-operators")]
    public async Task ShouldDenyAccessGivenMissingBlankOrUnheldAdministratorRole(string? configuredRole)
    {
        // Arrange
        var store = new StatusCheckpointStore();
        var progress = new StatusProgressReader();
        var services = Services(store);
        _ = services.AddSingleton<IConfiguration>(Configuration(configuredRole));
        _ = services.AddSingleton<IDomainEventProgressReader>(progress);
        _ = services.AddPortia().AddProjector<GlobalStatusProjector>("projection", WorkloadScope.Global);
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        // A missing configuration entry may use a host environment fallback. Keep the test actor
        // outside that fallback without mutating process-wide environment in parallel tests.
        var actorRole = configuredRole is null
            ? "unassigned-" + Environment.GetEnvironmentVariable("PORTIA_SYSTEM_ADMIN_ROLE")
            : AdministratorRole;

        // Act
        var actor = SystemAdminActor(role: actorRole);

        // Assert
        Assert.True(await provider.GetRequiredService<IOperationalAuthorizer>()
            .AuthorizeAsync(actor, OperationalScope.Global, OperationalAccess.Read));

        var result = await scope.ServiceProvider.GetRequiredService<IRequestBus>()
            .SendAsync(new GetProcessorStatus("projection"), actor);

        Assert.False(result.IsSuccess);
        Assert.Equal(RequestErrorKind.Forbidden, result.Error.Kind);
        Assert.Empty(store.Reads);
        Assert.Null(progress.Pattern);
    }

    /// <summary>Rejects unregistered, incorrectly scoped, and invalid targets before reading a checkpoint.</summary>
    /// <param name="processor">The requested component.</param>
    /// <param name="tenant">The requested tenant, or null for a global inspection.</param>
    /// <param name="error">The expected refusal classification.</param>
    [Theory]
    [InlineData("missing", null, RequestErrorKind.NotFound)]
    [InlineData("global-projector", "tenant-a", RequestErrorKind.Validation)]
    [InlineData("tenant-projector", null, RequestErrorKind.Validation)]
    [InlineData("tenant-projector", "invalid/tenant", RequestErrorKind.Validation)]
    public async Task ShouldRejectBeforeResolvingCheckpointGivenInvalidTarget(string processor, string? tenant,
        RequestErrorKind error)
    {
        // Arrange
        var store = new StatusCheckpointStore();
        var services = Services(store);
        _ = services.AddSingleton<ITenantDirectory>(new FixedTenantDirectory(new TenantId("tenant-a")));
        _ = services.AddPortia()
            .AddProjector<GlobalStatusProjector>("global-projector", WorkloadScope.Global)
            .AddProjector<TenantStatusProjector>("tenant-projector", WorkloadScope.PerTenant);
        await using var provider = services.BuildServiceProvider();

        // Act
        var result = await SendAsync(provider, new GetProcessorStatus(processor, tenant), tenant ?? "global");

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(error, result.Error.Kind);
        Assert.Empty(store.Reads);
    }

    /// <summary>Inspects a tenant checkpoint without replaying, updating, or depending on the tenant directory.</summary>
    [Fact]
    public async Task ShouldReadTenantStatusGivenTenantDirectoryNotConsulted()
    {
        // Arrange
        var store = new StatusCheckpointStore();
        var pattern = EventStreamPattern.ForPattern("tenant-a", "events");
        var identity = new CheckpointIdentity("tenant-projection", pattern);
        store.Set(identity, "native-tenant-progress");
        var directory = new UnavailableStatusTenantDirectory();
        var services = Services(store);
        _ = services.AddSingleton<ITenantDirectory>(directory);
        _ = services.AddSingleton<IDomainEventProgressReader>(new StatusProgressReader());
        _ = services.AddPortia().AddProjector<TenantStatusProjector>(identity.ComponentName, WorkloadScope.PerTenant);
        await using var provider = services.BuildServiceProvider();

        // Act
        var result = await SendAsync(provider, new GetProcessorStatus(identity.ComponentName, "tenant-a"), "tenant-a");

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal("native-tenant-progress", result.Value.Progress.Position.Value);
        Assert.Equal(pattern.ToString(), result.Value.Pattern);
        Assert.Equal(identity, Assert.Single(store.Reads));
        Assert.Equal(0, directory.Reads);
        Assert.Equal(0, store.Writes);
    }

    /// <summary>Retains the native checkpoint when an adapter returns an unrelated progress position.</summary>
    [Fact]
    public async Task ShouldRejectObservationGivenSourceObservationReplacingAuthoritativeCheckpoint()
    {
        // Arrange
        var store = new StatusCheckpointStore();
        store.Set(new CheckpointIdentity("projection", EventStreamPattern.ForPattern("global", "events")), "native-progress");
        var services = Services(store);
        _ = services.AddSingleton<IDomainEventProgressReader>(new MismatchedStatusProgressReader());
        _ = services.AddPortia().AddProjector<GlobalStatusProjector>("projection", WorkloadScope.Global);
        await using var provider = services.BuildServiceProvider();

        // Act
        var result = await SendAsync(provider, new GetProcessorStatus("projection"));

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal("native-progress", result.Value.Progress.Position.Value);
        Assert.Equal(nameof(InvalidOperationException), result.Value.SourceError);
        Assert.Null(result.Value.Progress.SourcePosition);
        Assert.Null(result.Value.Progress.PendingEvents);
        Assert.Null(result.Value.Progress.IsCaughtUp);
        Assert.Equal(0, store.Writes);
    }

    /// <summary>Preserves native progress and includes only matching failure evidence, even when the source is unavailable.</summary>
    /// <param name="staleJournal">Whether journal progress differs from the native checkpoint.</param>
    /// <param name="sourceUnavailable">Whether the source observation fails.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ShouldUseJournalOnlyForFailureEvidenceGivenAuthoritativeCheckpoint(bool staleJournal,
        bool sourceUnavailable)
    {
        // Arrange
        var store = new StatusCheckpointStore();
        var pattern = EventStreamPattern.ForPattern("global", "events");
        var identity = new CheckpointIdentity("projection", pattern);
        store.Set(identity, "native-progress");
        var attemptId = Guid.NewGuid();
        var checkpoint = staleJournal ? "old-journal-progress" : "native-progress";
        var completed = DateTimeOffset.UtcNow;
        var target = new OperationalTarget(identity.ComponentName, pattern.ToString(), OperationalScope.Global);
        var attempt = new OperationalAttempt(attemptId, Guid.NewGuid(), completed.AddSeconds(-1), completed,
            "failed", checkpoint, "uncommitted-next", [], [], "test", new ApplicationTopology([], [], "test"),
            "Handling", "Validation", false);
        var state = new OperationalProcessorState(target, "projector", attempt.OwnerSession, 1, checkpoint,
            new OperationalProcessingSettings(1, 1, 10, 1, 1), [attempt], new OperationalBlock(attemptId, 1),
            [], [], [], 2, completed);
        var services = Services(store);
        _ = services.AddSingleton<IOperationalStore>(new StatusJournal(state));
        if (sourceUnavailable)
            _ = services.AddSingleton<IDomainEventProgressReader>(new FailingStatusProgressReader());
        _ = services.AddPortia().AddProjector<GlobalStatusProjector>(identity.ComponentName, WorkloadScope.Global);
        await using var provider = services.BuildServiceProvider();

        // Act
        var result = await SendAsync(provider, new GetProcessorStatus(identity.ComponentName));

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal("native-progress", result.Value.Progress.Position.Value);
        Assert.Equal(sourceUnavailable ? nameof(IOException) : null, result.Value.SourceError);
        Assert.Null(result.Value.Progress.SourcePosition);
        Assert.Null(result.Value.Progress.PendingEvents);
        Assert.Null(result.Value.Progress.IsCaughtUp);
        if (staleJournal)
            Assert.Null(result.Value.Failure);
        else
        {
            Assert.NotNull(result.Value.Failure);
            Assert.Equal("Handling", result.Value.Failure.Stage);
            Assert.Equal("Validation", result.Value.Failure.Kind);
            Assert.True(result.Value.Failure.IsBlocked);
            Assert.Equal(2, result.Value.Failure.ConsecutiveFailures);
            Assert.Equal(completed, result.Value.Failure.FailedAt);
        }
        Assert.Equal(0, store.Writes);
        var options = provider.GetRequiredKeyedService<JsonSerializerOptions>(PortiaServiceKeys.Json);
        Assert.DoesNotContain("private source detail", JsonSerializer.Serialize(result.Value, options), StringComparison.Ordinal);
    }

    /// <summary>Preserves caller cancellation instead of converting it to an unknown source observation.</summary>
    [Fact]
    public async Task ShouldPropagateCancellationGivenCallerCancelledDuringSourceObservation()
    {
        // Arrange
        var store = new StatusCheckpointStore();
        var services = Services(store);
        using var cancellation = new CancellationTokenSource();
        _ = services.AddSingleton<IDomainEventProgressReader>(new FailingStatusProgressReader(cancellation));
        _ = services.AddPortia().AddProjector<GlobalStatusProjector>("projection", WorkloadScope.Global);
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        // Act
        var bus = scope.ServiceProvider.GetRequiredService<IRequestBus>();

        // Assert
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await bus.SendAsync(new GetProcessorStatus("projection"), SystemAdminActor(), cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Single(store.Reads);
        Assert.Equal(0, store.Writes);
    }

    /// <summary>Preserves progress when failure evidence cannot be read, including a combined source outage.</summary>
    /// <param name="sourceUnavailable">Whether the source probe also fails.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldPreserveCheckpointAndSourceObservationGivenUnavailableJournal(bool sourceUnavailable)
    {
        // Arrange
        var store = new StatusCheckpointStore();
        store.Set(new CheckpointIdentity("projection", EventStreamPattern.ForPattern("global", "events")), "native-progress");
        var services = Services(store);
        _ = services.AddSingleton<IDomainEventProgressReader>(sourceUnavailable
            ? new FailingStatusProgressReader() : new StatusProgressReader());
        _ = services.AddSingleton<IOperationalStore>(new UnavailableStatusJournal());
        _ = services.AddPortia().AddProjector<GlobalStatusProjector>("projection", WorkloadScope.Global);
        await using var provider = services.BuildServiceProvider();

        // Act
        var result = await SendAsync(provider, new GetProcessorStatus("projection"));

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal("native-progress", result.Value.Progress.Position.Value);
        Assert.Equal(sourceUnavailable ? nameof(IOException) : null, result.Value.SourceError);
        Assert.Equal(sourceUnavailable ? null : 3UL, result.Value.Progress.PendingEvents);
        Assert.Null(result.Value.Failure);
        Assert.Equal(nameof(IOException), result.Value.FailureError);
        Assert.Single(store.Reads);
        Assert.Equal(0, store.Writes);
        var options = provider.GetRequiredKeyedService<JsonSerializerOptions>(PortiaServiceKeys.Json);
        Assert.DoesNotContain("private journal detail", JsonSerializer.Serialize(result.Value, options), StringComparison.Ordinal);
    }

    /// <summary>Preserves caller cancellation while reading operational failure evidence.</summary>
    [Fact]
    public async Task ShouldPropagateCancellationGivenCallerCancelledDuringJournalObservation()
    {
        // Arrange
        var store = new StatusCheckpointStore();
        var services = Services(store);
        var progress = new StatusProgressReader();
        using var cancellation = new CancellationTokenSource();
        _ = services.AddSingleton<IDomainEventProgressReader>(progress);
        _ = services.AddSingleton<IOperationalStore>(new UnavailableStatusJournal(cancellation));
        _ = services.AddPortia().AddProjector<GlobalStatusProjector>("projection", WorkloadScope.Global);
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        // Act
        var bus = scope.ServiceProvider.GetRequiredService<IRequestBus>();

        // Assert
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await bus.SendAsync(new GetProcessorStatus("projection"), SystemAdminActor(), cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.NotNull(progress.Pattern);
        Assert.Single(store.Reads);
        Assert.Equal(0, store.Writes);
    }

    /// <summary>Provides one built-in contract with source-generated metadata for all registered components.</summary>
    [Fact]
    public void ShouldRegisterStatusOnceWithReflectionFreeJsonGivenEveryComponent()
    {
        // Arrange
        var services = Services(new StatusCheckpointStore());
        _ = services.AddPortia()
            .AddProjector<GlobalStatusProjector>("projection", WorkloadScope.Global)
            .AddReactor<GlobalStatusReactor>("reaction", WorkloadScope.Global);

        // Act
        using var provider = services.BuildServiceProvider();

        // Assert
        Assert.Single(provider.GetServices<RequestHandlerRegistration>(), item => item.RequestType == typeof(GetProcessorStatus));
        Assert.Single(provider.GetServices<RequestAuthorizerRegistration>(), item => item.RequestType == typeof(GetProcessorStatus));
        Assert.Single(provider.GetServices<RequestTransportRegistration>(), item => item.RequestType == typeof(GetProcessorStatus));
        var options = provider.GetRequiredKeyedService<JsonSerializerOptions>(PortiaServiceKeys.Json);
        var request = new GetProcessorStatus("projection", "tenant-a");
        var json = JsonSerializer.Serialize(request, options);
        Assert.Equal(request, JsonSerializer.Deserialize<GetProcessorStatus>(json, options));
        var status = new ProcessorStatus("projection", "projector", "tenant-a", "stream://tenant-a/events/*", null,
            new EventStreamProgress(new EventCursor("checkpoint"), new EventCursor("head"), 3, false, DateTimeOffset.UtcNow), null);
        Assert.Equal(status, JsonSerializer.Deserialize<ProcessorStatus>(JsonSerializer.Serialize(status, options), options));
    }

    static ServiceCollection Services(StatusCheckpointStore store)
    {
        var services = new ServiceCollection();
        _ = services.AddSingleton(store);
        _ = services.AddSingleton<IProjectionCheckpointStore>(store);
        _ = services.AddSingleton<IOperationalAuthorizer, TestOperationalAuthorizer>();
        _ = services.AddSingleton<IConfiguration>(Configuration());
        return services;
    }

    static IConfiguration Configuration(string? role = AdministratorRole) => new ConfigurationBuilder()
        .AddInMemoryCollection(role is null ? [] : new Dictionary<string, string?> { ["PORTIA_SYSTEM_ADMIN_ROLE"] = role })
        .Build();

    static async ValueTask<Result<ProcessorStatus>> SendAsync(IServiceProvider provider, GetProcessorStatus request,
        string actorScope = "global")
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IRequestBus>()
            .SendAsync(request, SystemAdminActor(actorScope));
    }

    static ClaimsPrincipal SystemAdminActor(string scope = "global", string role = AdministratorRole)
    {
        var actor = TestOperationalAuthorizer.Actor(scope: scope);
        ((ClaimsIdentity)actor.Identity!).AddClaim(new Claim(ClaimTypes.Role, role));
        return actor;
    }

    sealed class StatusCheckpointStore : IProjectionStore, IProjectionCheckpointStore
    {
        readonly Dictionary<CheckpointIdentity, ProjectionCheckpoint> _checkpoints = [];
        internal List<CheckpointIdentity> Reads { get; } = [];
        internal int Writes { get; private set; }

        internal void Set(CheckpointIdentity identity, string cursor) => _checkpoints[identity] = new ProjectionCheckpoint(new EventCursor(cursor));

        public ValueTask<ProjectionCheckpoint> LoadCheckpointAsync(CheckpointIdentity identity, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Reads.Add(identity);
            return ValueTask.FromResult(_checkpoints.GetValueOrDefault(identity));
        }

        public ValueTask<ProjectionCheckpoint> LoadAsync(CheckpointIdentity identity, CancellationToken ct = default) =>
            LoadCheckpointAsync(identity, ct);

        public ValueTask<IProjectionBatch> BeginAsync(ProjectionBatchContext context, CancellationToken ct = default)
        {
            Writes++;
            throw new InvalidOperationException("Status inspection must not begin processing.");
        }

        public ValueTask SaveAsync(CheckpointIdentity identity, ProjectionCheckpoint checkpoint, CancellationToken ct = default)
        {
            Writes++;
            throw new InvalidOperationException("Status inspection must not change progress.");
        }
    }

    sealed class GlobalStatusProjector(StatusCheckpointStore store, WorkloadContext workload)
        : Projector(store, Pattern(workload));

    sealed class TenantStatusProjector(StatusCheckpointStore store, WorkloadContext workload)
        : Projector(store, Pattern(workload, true));

    sealed class GlobalStatusReactor(StatusCheckpointStore store, WorkloadContext workload)
        : Reactor(store, Pattern(workload));

    static EventStreamPattern Pattern(WorkloadContext workload, bool perTenant = false)
    {
        _ = workload.Identity;
        return perTenant ? EventStreamPattern.ForTenant("events") : EventStreamPattern.ForPattern("global", "events");
    }

    sealed class StatusProgressReader : IDomainEventProgressReader
    {
        internal EventStreamPattern? Pattern { get; private set; }
        internal EventCursor Position { get; private set; }

        public ValueTask<EventStreamProgress> GetProgressAsync(EventStreamPattern pattern, EventCursor position,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Pattern = pattern;
            Position = position;
            return ValueTask.FromResult(new EventStreamProgress(position, new EventCursor("opaque-head"), 3, false, DateTimeOffset.UtcNow));
        }
    }

    sealed class FailingStatusProgressReader(CancellationTokenSource? cancellation = null) : IDomainEventProgressReader
    {
        public ValueTask<EventStreamProgress> GetProgressAsync(EventStreamPattern pattern, EventCursor position,
            CancellationToken ct = default)
        {
            if (cancellation is not null)
            {
                cancellation.Cancel();
                ct.ThrowIfCancellationRequested();
            }
            throw new IOException("private source detail");
        }
    }

    sealed class MismatchedStatusProgressReader : IDomainEventProgressReader
    {
        public ValueTask<EventStreamProgress> GetProgressAsync(EventStreamPattern pattern, EventCursor position,
            CancellationToken ct = default) => ValueTask.FromResult(new EventStreamProgress(
            new EventCursor("unrelated-position"), new EventCursor("unrelated-head"), 0, true, DateTimeOffset.UtcNow));
    }

    sealed class UnavailableStatusTenantDirectory : ITenantDirectory
    {
        internal int Reads { get; private set; }

        public IAsyncEnumerable<TenantId> GetActiveTenantsAsync(CancellationToken ct = default)
        {
            Reads++;
            throw new IOException("A status query must not read or update tenant lifecycle progress.");
        }

        public IAsyncEnumerable<TenantLifecycleChange> WatchAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("A status query must not subscribe to tenant lifecycle changes.");
    }

    sealed class StatusJournal(OperationalProcessorState state) : IOperationalStore
    {
        public ValueTask<OperationalProcessorState?> ReadAsync(OperationalTarget target, CancellationToken ct = default) =>
            ValueTask.FromResult<OperationalProcessorState?>(target == state.Target ? state : null);

        public ValueTask<IReadOnlyList<OperationalProcessorState>> ListAsync(OperationalScope scope, CancellationToken ct = default) =>
            ValueTask.FromResult<IReadOnlyList<OperationalProcessorState>>([state]);

        public ValueTask<bool> CompareExchangeAsync(OperationalProcessorState replacement, long expectedRevision,
            Guid expectedOwner, CancellationToken ct = default) => throw new InvalidOperationException("Status inspection must not change the journal.");
    }

    sealed class UnavailableStatusJournal(CancellationTokenSource? cancellation = null) : IOperationalStore
    {
        public ValueTask<OperationalProcessorState?> ReadAsync(OperationalTarget target, CancellationToken ct = default)
        {
            if (cancellation is not null)
            {
                cancellation.Cancel();
                ct.ThrowIfCancellationRequested();
            }
            throw new IOException("private journal detail");
        }

        public ValueTask<IReadOnlyList<OperationalProcessorState>> ListAsync(OperationalScope scope,
            CancellationToken ct = default) => throw new InvalidOperationException("Status inspection must read one exact target.");

        public ValueTask<bool> CompareExchangeAsync(OperationalProcessorState replacement, long expectedRevision,
            Guid expectedOwner, CancellationToken ct = default) => throw new InvalidOperationException("Status inspection must not change the journal.");
    }
}
