using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cntryl.Portia;

/// <summary>Verifies deployment-level Fitz listener discovery from selected request handlers.</summary>
public sealed class PortiaFitzBuilderTests
{
    /// <summary>An application-supplied coordinator owns workload identity independently of Fitz.</summary>
    [Fact]
    public async Task ShouldAllowWorkloadsGivenCustomCoordinatorWithoutFleetIdentity()
    {
        // Arrange
        var services = new ServiceCollection();
        _ = services.AddSingleton<IWorkloadCoordinator, CustomCoordinator>();
        _ = services.AddSingleton<IEventStore>(new InMemoryEventStore());
        var application = services.AddPortia();
        _ = application.AddProjector<TestProjector>("projection", WorkloadScope.Global);
        _ = application.AddFitz(new ClientConfig(new Uri("ws://127.0.0.1:1/ws"))).AddWorkers();

        // Act
        await using var provider = services.BuildServiceProvider();

        // Assert
        var lifecycle = Assert.IsAssignableFrom<IHostedLifecycleService>(
            Assert.Single(provider.GetServices<IHostedService>(), service => service is FitzApplicationWorkers));

        await lifecycle.StartingAsync(default);
    }

    /// <summary>Workloads using the effective Fitz coordinator still require a fleet identity.</summary>
    [Fact]
    public async Task ShouldRejectWorkloadsGivenFitzCoordinationWithoutFleetIdentity()
    {
        // Arrange
        var services = new ServiceCollection();
        _ = services.AddSingleton<IEventStore>(new InMemoryEventStore());
        var application = services.AddPortia();
        _ = application.AddProjector<TestProjector>("projection", WorkloadScope.Global);
        _ = application.AddFitz(new ClientConfig(new Uri("ws://127.0.0.1:1/ws"))).AddWorkers();

        // Act
        await using var provider = services.BuildServiceProvider();

        // Assert
        var lifecycle = Assert.IsAssignableFrom<IHostedLifecycleService>(
            Assert.Single(provider.GetServices<IHostedService>(), service => service is FitzApplicationWorkers));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.StartingAsync(default));

        Assert.Contains("Fitz:ApplicationName", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Queue workers can start without an optional terminal failure observer.</summary>
    [Fact]
    public async Task ShouldAllowQueueWorkerStartupGivenNoTerminalHandler()
    {
        // Arrange
        var services = new ServiceCollection();
        _ = services.AddSingleton<IRequestActorValidator, TestRequestActorValidator>();
        var application = services.AddPortia();
        _ = application.AddGeneratedHandler(new RequestRegistration<FitzHostedRequest, FitzHostedHandler>());
        _ = application.AddGeneratedRequest(Transport<FitzHostedRequest>([RequestTransportId.Queue]));
        _ = application.AddFitz(new ClientConfig(new Uri("ws://127.0.0.1:1/ws")),
            fitz => _ = fitz.AddQueueWorkers()).AddWorkers();

        // Act
        await using var provider = services.BuildServiceProvider();

        // Assert
        var lifecycle = Assert.IsAssignableFrom<IHostedLifecycleService>(
            Assert.Single(provider.GetServices<IHostedService>(), service => service is FitzApplicationWorkers));

        await lifecycle.StartingAsync(default);
    }

    /// <summary>A scoped terminal observer remains available to queue workers.</summary>
    [Fact]
    public async Task ShouldAllowQueueWorkerStartupGivenScopedTerminalHandler()
    {
        // Arrange
        var services = new ServiceCollection();
        _ = services.AddSingleton<IRequestActorValidator, TestRequestActorValidator>();
        _ = services.AddScoped<IQueuedRequestTerminalHandler, TestTerminalHandler>();
        var application = services.AddPortia();
        _ = application.AddGeneratedHandler(new RequestRegistration<FitzHostedRequest, FitzHostedHandler>());
        _ = application.AddGeneratedRequest(Transport<FitzHostedRequest>([RequestTransportId.Queue]));
        _ = application.AddFitz(new ClientConfig(new Uri("ws://127.0.0.1:1/ws")),
            fitz => _ = fitz.AddQueueWorkers()).AddWorkers();

        // Act
        await using var provider = services.BuildServiceProvider();

        // Assert
        var lifecycle = Assert.IsAssignableFrom<IHostedLifecycleService>(
            Assert.Single(provider.GetServices<IHostedService>(), service => service is FitzApplicationWorkers));

        await lifecycle.StartingAsync(default);
    }

    /// <summary>Fitz workers do not begin consuming before grouped HTTP endpoint validation completes.</summary>
    [Fact]
    public async Task ShouldDeferFitzWorkersGivenEndpointValidationPending()
    {
        // Arrange
        var services = new ServiceCollection();
        _ = services.AddSingleton<IRequestActorValidator, TestRequestActorValidator>();
        _ = services.AddScoped<IQueuedRequestTerminalHandler, TestTerminalHandler>();
        var application = services.AddPortia();
        _ = application.AddGeneratedHandler(new RequestRegistration<FitzHostedRequest, FitzHostedHandler>());
        _ = application.AddGeneratedRequest(Transport<FitzHostedRequest>([RequestTransportId.Queue]));
        await using var client = new FitzClient(new ClientConfig(new Uri("ws://127.0.0.1:1/ws")));
        _ = application.UseFitzClient(client, fitz => _ = fitz.AddQueueWorkers()).AddWorkers();
        await using var provider = services.BuildServiceProvider();

        // Act
        var validation = provider.GetRequiredService<PortiaStartupValidationRegistry>().BeginEndpointValidation();

        // Assert
        var worker = Assert.IsType<FitzApplicationWorkers>(
            Assert.Single(provider.GetServices<IHostedService>(), service => service is FitzApplicationWorkers));

        await worker.StartAsync(default);

        Assert.Null(worker.ExecuteTask);
        validation.Complete();
        await worker.StartedAsync(default);
        Assert.NotNull(worker.ExecuteTask);
        await worker.StopAsync(default);
    }

    /// <summary>A worker stopped while its deferred start waits for validation must not start afterwards.</summary>
    [Fact]
    public async Task ShouldNotStartFitzWorkersGivenStopWhileValidationPending()
    {
        // Arrange
        var services = new ServiceCollection();
        _ = services.AddSingleton<IRequestActorValidator, TestRequestActorValidator>();
        _ = services.AddScoped<IQueuedRequestTerminalHandler, TestTerminalHandler>();
        var application = services.AddPortia();
        _ = application.AddGeneratedHandler(new RequestRegistration<FitzHostedRequest, FitzHostedHandler>());
        _ = application.AddGeneratedRequest(Transport<FitzHostedRequest>([RequestTransportId.Queue]));
        await using var client = new FitzClient(new ClientConfig(new Uri("ws://127.0.0.1:1/ws")));
        _ = application.UseFitzClient(client, fitz => _ = fitz.AddQueueWorkers()).AddWorkers();
        await using var provider = services.BuildServiceProvider();
        var validation = provider.GetRequiredService<PortiaStartupValidationRegistry>().BeginEndpointValidation();
        var worker = Assert.IsType<FitzApplicationWorkers>(
            Assert.Single(provider.GetServices<IHostedService>(), service => service is FitzApplicationWorkers));
        await worker.StartAsync(default);
        var started = worker.StartedAsync(default);

        // Act
        await worker.StopAsync(default);
        validation.Complete();
        await started.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        try
        {
            Assert.Null(worker.ExecuteTask);
        }
        finally
        {
            await worker.StopAsync(default);
        }
    }

    /// <summary>The composition root is public, so a caller locking it must not block Fitz registration.</summary>
    [Fact]
    public async Task ShouldRegisterFitzGivenCallerHoldingApplicationLock()
    {
        // Arrange
        var application = new ServiceCollection().AddPortia();
        await using var client = new FitzClient(new ClientConfig(new Uri("ws://127.0.0.1:1/ws")));
        var locked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var holder = Task.Run(() =>
        {
            lock (application)
            {
                _ = locked.TrySetResult();
                _ = release.Wait(TimeSpan.FromSeconds(30));
            }
        });
        await locked.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Act
        var registration = Task.Run(() => application.UseFitzClient(client));
        var failure = await Record.ExceptionAsync(() => registration.WaitAsync(TimeSpan.FromSeconds(5)));

        // Assert
        try
        {
            Assert.Null(failure);
        }
        finally
        {
            release.Set();
            await holder;
        }
    }

    /// <summary>Request listeners do not need workload fleet identity when no component is hosted.</summary>
    [Fact]
    public async Task ShouldAllowRequestOnlyWorkerStartupGivenNoFleetIdentity()
    {
        // Arrange
        var services = new ServiceCollection();
        _ = services.AddSingleton<IRequestActorValidator, TestRequestActorValidator>();
        var application = services.AddPortia();
        _ = application.AddGeneratedHandler(new RequestRegistration<FitzHostedRequest, FitzHostedHandler>());
        _ = application.AddGeneratedRequest(Transport<FitzHostedRequest>([RequestTransportId.Callable]));
        _ = application.AddFitz(new ClientConfig(new Uri("ws://127.0.0.1:1/ws")),
            fitz => _ = fitz.AddRpcWorkers()).AddWorkers();

        // Act
        await using var provider = services.BuildServiceProvider();

        // Assert
        var lifecycle = Assert.IsAssignableFrom<IHostedLifecycleService>(
            Assert.Single(provider.GetServices<IHostedService>(), service => service is FitzApplicationWorkers));

        await lifecycle.StartingAsync(default);
    }

    /// <summary>Fitz rejects terminal thresholds before connecting because attempts are unavailable.</summary>
    [Fact]
    public async Task ShouldRejectFitzStartupGivenPositiveTerminalAttempt()
    {
        // Arrange
        var services = new ServiceCollection();
        _ = services.Configure<QueueRunnerOptions>(options => options.TerminalAttempt = 2);
        var application = services.AddPortia();
        _ = application.AddGeneratedHandler(new RequestRegistration<FitzHostedRequest, FitzHostedHandler>());
        _ = application.AddGeneratedRequest(Transport<FitzHostedRequest>([RequestTransportId.Queue]));
        _ = application.AddFitz(new ClientConfig(new Uri("ws://127.0.0.1:1/ws")),
            fitz => _ = fitz.AddQueueWorkers()).AddWorkers();

        // Act
        await using var provider = services.BuildServiceProvider();

        // Assert
        var lifecycle = Assert.IsAssignableFrom<IHostedLifecycleService>(
            Assert.Single(provider.GetServices<IHostedService>(), service => service is FitzApplicationWorkers));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.StartingAsync(default));

        Assert.Contains("does not expose durable attempt counts", error.Message, StringComparison.Ordinal);
    }

    /// <summary>The native schedule API is published from Portia's shared Fitz connection.</summary>
    [Fact]
    public void ShouldRegisterNativeScheduleClientAliasGivenRequestClients()
    {
        // Arrange
        var services = new ServiceCollection();
        var fitz = new PortiaFitzBuilder(services.AddPortia());

        // Act
        _ = fitz.AddRequestClients();

        // Assert
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IScheduleClient));
    }

    /// <summary>An explicit transport selector is deployment intent, not an optional filter.</summary>
    [Fact]
    public void ShouldFailGivenNoMatchingHandlersWhenQueueWorkersAreSelected()
    {
        // Arrange
        var fitz = new PortiaFitzBuilder(new ServiceCollection().AddPortia());

        // Act
        _ = fitz.AddQueueWorkers();

        // Assert
        var error = Assert.Throws<InvalidOperationException>(() => _ = fitz.Workers);

        Assert.Contains("AddQueueWorkers()", error.Message, StringComparison.Ordinal);
        Assert.Contains("no queuable request handlers", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Invalid explicit intent must fail before attempting to connect to infrastructure.</summary>
    [Fact]
    public async Task ShouldFailBeforeConnectingGivenNoMatchingHandlersWhenWorkerHostStarts()
    {
        // Arrange
        var builder = Host.CreateApplicationBuilder();
        var application = builder.Services.AddPortia();
        _ = application.AddGeneratedHandler(new RequestRegistration<FitzHostedRequest, FitzHostedHandler>());
        _ = application.AddGeneratedRequest(Transport<FitzHostedRequest>([RequestTransportId.Callable]));
        _ = application
            .AddFitz(new ClientConfig(new Uri("ws://127.0.0.1:1/ws")), fitz => _ = fitz.AddQueueWorkers())
            .AddWorkers();

        // Act
        using var host = builder.Build();

        // Assert
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());

        Assert.Contains("AddQueueWorkers()", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Every explicitly composed selector must resolve independently.</summary>
    [Fact]
    public void ShouldIdentifyMissingNoticeHandlersGivenQueueAndNoticeSelectorsWhenWorkersAreResolved()
    {
        // Arrange
        var services = new ServiceCollection();
        var application = services.AddPortia();
        _ = application.AddGeneratedHandler(new RequestRegistration<FitzHostedRequest, FitzHostedHandler>());
        _ = application.AddGeneratedRequest(Transport<FitzHostedRequest>([RequestTransportId.Queue]));
        var fitz = new PortiaFitzBuilder(application);

        // Act
        _ = fitz.AddQueueWorkers().AddNoticeWorkers();

        // Assert
        var error = Assert.Throws<InvalidOperationException>(() => _ = fitz.Workers);

        Assert.Contains("AddNoticeWorkers()", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("AddQueueWorkers()", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Default discovery may legitimately find no inbound request handlers.</summary>
    [Fact]
    public void ShouldCreateNoRequestWorkersGivenDefaultSelectionWithNoHandlersWhenWorkersAreResolved()
    {
        // Arrange
        var fitz = new PortiaFitzBuilder(new ServiceCollection().AddPortia());

        // Act
        var workers = fitz.Workers;

        // Assert
        Assert.Empty(workers);
    }

    /// <summary>Fleet configuration alone must not silently disable the default request listeners.</summary>
    [Fact]
    public void ShouldKeepDefaultRequestWorkersGivenFleetOnlyConfigurationWhenWorkersAreResolved()
    {
        // Arrange
        var services = new ServiceCollection();
        var application = services.AddPortia();
        _ = application.AddGeneratedHandler(new RequestRegistration<FitzHostedRequest, FitzHostedHandler>());
        _ = application.AddGeneratedRequest(
            Transport<FitzHostedRequest>([RequestTransportId.Callable, RequestTransportId.Queue]));
        var fitz = new PortiaFitzBuilder(application);

        // Act
        _ = fitz.UseFleet(new FleetRunOptions { MembershipSelector = "lease://app/members/*" });

        // Assert
        Assert.Equal(
            [new FitzRpcWorkerDefinition(), new FitzQueueWorkerDefinition("queue://app/accounts/*")],
            fitz.Workers);
    }

    /// <summary>A workload-only deployment can explicitly opt out of the default request listeners.</summary>
    [Fact]
    public void ShouldCreateNoRequestWorkersGivenExplicitDisableWhenWorkersAreResolved()
    {
        // Arrange
        var services = new ServiceCollection();
        var application = services.AddPortia();
        _ = application.AddGeneratedHandler(new RequestRegistration<FitzHostedRequest, FitzHostedHandler>());
        _ = application.AddGeneratedRequest(Transport<FitzHostedRequest>([RequestTransportId.Queue]));
        var fitz = new PortiaFitzBuilder(application);

        // Act
        _ = fitz.DisableRequestWorkers();

        // Assert
        Assert.Empty(fitz.Workers);
    }

    /// <summary>All declared transports are activated once while outbound-only requests are excluded.</summary>
    [Fact]
    public void ShouldCreateDeclaredWorkersOnceGivenSelectedHandlersWhenRequestWorkersAreAdded()
    {
        // Arrange
        var services = new ServiceCollection();
        var application = services.AddPortia();
        _ = application.AddGeneratedHandler(new RequestRegistration<FitzHostedRequest, FitzHostedHandler>());
        _ = application.AddGeneratedRequest(Transport<FitzHostedRequest>([
            RequestTransportId.Callable, RequestTransportId.Queue, RequestTransportId.Notice,
            RequestTransportId.Schedule
        ]));
        _ = application.AddGeneratedRequest(Transport<FitzOutboundOnlyRequest>([RequestTransportId.Queue]));
        var fitz = new PortiaFitzBuilder(application);

        // Act
        _ = fitz.AddRequestWorkers().AddRequestWorkers();

        // Assert
        Assert.Equal(
            [
                new FitzRpcWorkerDefinition(),
                new FitzQueueWorkerDefinition("queue://app/accounts/*"),
                new FitzNoticeWorkerDefinition("notice://app/accounts/*"),
                new FitzScheduleWorkerDefinition("schedule://app/accounts/*/run")
            ],
            fitz.Workers);
    }

    /// <summary>Worker declaration is application intent and must not depend on composition order.</summary>
    [Fact]
    public void ShouldIncludeLaterHandlersGivenWorkersDeclaredBeforeHandlersWhenWorkersAreResolved()
    {
        // Arrange
        var services = new ServiceCollection();
        var application = services.AddPortia();
        var fitz = new PortiaFitzBuilder(application);
        _ = fitz.AddRequestWorkers();

        _ = application.AddGeneratedHandler(new RequestRegistration<FitzHostedRequest, FitzHostedHandler>());

        // Act
        _ = application.AddGeneratedRequest(Transport<FitzHostedRequest>([RequestTransportId.Queue]));

        // Assert
        Assert.Equal([new FitzQueueWorkerDefinition("queue://app/accounts/*")], fitz.Workers);
    }

    /// <summary>A deployment can activate one transport kind without repeating request types.</summary>
    [Fact]
    public void ShouldCreateOnlyQueueWorkersGivenMultiTransportHandlerWhenQueueWorkersAreAdded()
    {
        // Arrange
        var services = new ServiceCollection();
        var application = services.AddPortia();
        _ = application.AddGeneratedHandler(new RequestRegistration<FitzHostedRequest, FitzHostedHandler>());
        _ = application.AddGeneratedRequest(
            Transport<FitzHostedRequest>([RequestTransportId.Callable, RequestTransportId.Queue]));
        var fitz = new PortiaFitzBuilder(application);

        // Act
        _ = fitz.AddQueueWorkers();

        // Assert
        Assert.Equal([new FitzQueueWorkerDefinition("queue://app/accounts/*")], fitz.Workers);
    }

    /// <summary>Transport-specific selectors compose after the first one narrows the default.</summary>
    [Fact]
    public void ShouldCreateSelectedWorkerUnionGivenMultipleTransportSelectorsWhenWorkersAreResolved()
    {
        // Arrange
        var services = new ServiceCollection();
        var application = services.AddPortia();
        _ = application.AddGeneratedHandler(new RequestRegistration<FitzHostedRequest, FitzHostedHandler>());
        _ = application.AddGeneratedRequest(Transport<FitzHostedRequest>([
            RequestTransportId.Callable, RequestTransportId.Queue, RequestTransportId.Notice
        ]));
        var fitz = new PortiaFitzBuilder(application);

        // Act
        _ = fitz.AddQueueWorkers().AddNoticeWorkers();

        // Assert
        Assert.Equal(
            [
                new FitzQueueWorkerDefinition("queue://app/accounts/*"),
                new FitzNoticeWorkerDefinition("notice://app/accounts/*")
            ],
            fitz.Workers);
    }

    /// <summary>The first resolved worker set is the immutable hosting plan for that deployment.</summary>
    [Fact]
    public void ShouldKeepWorkerSnapshotStableGivenLaterRegistrationsWhenWorkersAreReadAgain()
    {
        // Arrange
        var services = new ServiceCollection();
        var application = services.AddPortia();
        _ = application.AddGeneratedHandler(new RequestRegistration<FitzHostedRequest, FitzHostedHandler>());
        _ = application.AddGeneratedRequest(Transport<FitzHostedRequest>([RequestTransportId.Queue]));
        var fitz = new PortiaFitzBuilder(application);
        _ = fitz.AddQueueWorkers();

        var first = fitz.Workers;
        _ = application.AddGeneratedHandler(
            new RequestRegistration<FitzOutboundOnlyRequest, FitzOutboundOnlyHandler>());

        // Act
        _ = application.AddGeneratedRequest(Transport<FitzOutboundOnlyRequest>([RequestTransportId.Queue]));

        // Assert
        Assert.Same(first, fitz.Workers);
        Assert.Equal([new FitzQueueWorkerDefinition("queue://app/accounts/*")], fitz.Workers);
    }

    /// <summary>The RPC server must not expose inferred requests that have no selected local handler.</summary>
    [Fact]
    public async Task ShouldExcludeOutboundRequestsGivenNoSelectedHandlerWhenRpcWorkersAreRegistered()
    {
        // Arrange
        var calls = 0;
        var services = new ServiceCollection();
        _ = services.AddSingleton<RequestHandlerRegistration>(
            new RequestRegistration<FitzHostedRequest, FitzHostedHandler>());
        _ = services.AddSingleton<FitzHostedHandler>();
        _ = services.AddSingleton(new RequestTransportRegistration(typeof(FitzHostedRequest),
            [RequestTransportId.Callable],
            new RequestRouteAttribute("app", "accounts", "*", "hosted"), new DiscriminatorAttribute("test.fitz.hosted"),
            Register));
        _ = services.AddSingleton(new RequestTransportRegistration(typeof(FitzOutboundCallableRequest),
            [RequestTransportId.Callable],
            new RequestRouteAttribute("app", "accounts", "*", "outbound"),
            new DiscriminatorAttribute("test.fitz.outbound-callable"), Register));
        using var provider = services.BuildServiceProvider();
        var server =
            new FitzRpcRequestServer(new InMemoryRpcClient(), provider.GetRequiredService<IServiceScopeFactory>());

        // Act
        await using var workers = await server.RegisterRequestsAsync();

        // Assert
        Assert.Equal(1, calls);
        return;

        ValueTask<IAsyncDisposable> Register(IRequestRpcRegistrar registrar, CancellationToken ct)
        {
            calls++;
            return ValueTask.FromResult<IAsyncDisposable>(new NoopAsyncDisposable());
        }
    }

    static RequestTransportRegistration Transport<TRequest>(IEnumerable<RequestTransportId> transports)
        where TRequest : IRequestBase =>
        new(typeof(TRequest), transports, new RequestRouteAttribute("app", "accounts", "*", "run"),
            new DiscriminatorAttribute("test.fitz." + typeof(TRequest).Name));

    sealed class TestTerminalHandler : IQueuedRequestTerminalHandler
    {
        public ValueTask HandleAsync(QueuedRequestFailureContext context, CancellationToken ct = default) =>
            ValueTask.CompletedTask;
    }

    sealed class CustomCoordinator : IWorkloadCoordinator
    {
        public Task RunAsync(Func<IReadOnlyCollection<WorkloadIdentity>> workloads,
            Func<WorkloadIdentity, CancellationToken, Task> run, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
