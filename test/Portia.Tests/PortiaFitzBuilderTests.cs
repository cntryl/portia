using Cntryl.Fitz;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cntryl.Portia;

/// <summary>Verifies deployment-level Fitz listener discovery from selected request handlers.</summary>
public sealed class PortiaFitzBuilderTests
{
    /// <summary>An explicit transport selector is deployment intent, not an optional filter.</summary>
    [Fact]
    public void ShouldFailGivenNoMatchingHandlersWhenQueueWorkersAreSelected()
    {
        var fitz = new PortiaFitzBuilder(new ServiceCollection().AddPortia());
        _ = fitz.AddQueueWorkers();

        var error = Assert.Throws<InvalidOperationException>(() => _ = fitz.Workers);

        Assert.Contains("AddQueueWorkers()", error.Message, StringComparison.Ordinal);
        Assert.Contains("no queuable request handlers", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Invalid explicit intent must fail before attempting to connect to infrastructure.</summary>
    [Fact]
    public async Task ShouldFailBeforeConnectingGivenNoMatchingHandlersWhenWorkerHostStarts()
    {
        var builder = Host.CreateApplicationBuilder();
        var application = builder.Services.AddPortia();
        _ = application.AddGeneratedHandler(new RequestRegistration<FitzHostedRequest, FitzHostedHandler>());
        _ = application.AddGeneratedRequest(Transport<FitzHostedRequest>(RequestTransports.Callable));
        _ = application
            .AddFitz(new ClientConfig(new Uri("ws://127.0.0.1:1/ws")), fitz => _ = fitz.AddQueueWorkers())
            .AddWorkers();
        using var host = builder.Build();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());

        Assert.Contains("AddQueueWorkers()", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Every explicitly composed selector must resolve independently.</summary>
    [Fact]
    public void ShouldIdentifyMissingNoticeHandlersGivenQueueAndNoticeSelectorsWhenWorkersAreResolved()
    {
        var services = new ServiceCollection();
        var application = services.AddPortia();
        _ = application.AddGeneratedHandler(new RequestRegistration<FitzHostedRequest, FitzHostedHandler>());
        _ = application.AddGeneratedRequest(Transport<FitzHostedRequest>(RequestTransports.Queuable));
        var fitz = new PortiaFitzBuilder(application);
        _ = fitz.AddQueueWorkers().AddNoticeWorkers();

        var error = Assert.Throws<InvalidOperationException>(() => _ = fitz.Workers);

        Assert.Contains("AddNoticeWorkers()", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("AddQueueWorkers()", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Default discovery may legitimately find no inbound request handlers.</summary>
    [Fact]
    public void ShouldCreateNoRequestWorkersGivenDefaultSelectionWithNoHandlersWhenWorkersAreResolved()
    {
        var fitz = new PortiaFitzBuilder(new ServiceCollection().AddPortia());

        Assert.Empty(fitz.Workers);
    }

    /// <summary>Fleet configuration alone must not silently disable the default request listeners.</summary>
    [Fact]
    public void ShouldKeepDefaultRequestWorkersGivenFleetOnlyConfigurationWhenWorkersAreResolved()
    {
        var services = new ServiceCollection();
        var application = services.AddPortia();
        _ = application.AddGeneratedHandler(new RequestRegistration<FitzHostedRequest, FitzHostedHandler>());
        _ = application.AddGeneratedRequest(
            Transport<FitzHostedRequest>(RequestTransports.Callable | RequestTransports.Queuable));
        var fitz = new PortiaFitzBuilder(application);

        _ = fitz.UseFleet(new FleetRunOptions { MembershipSelector = "lease://app/members/*" });

        Assert.Equal(
            [new FitzRpcWorkerDefinition(), new FitzQueueWorkerDefinition("queue://app/accounts/*")],
            fitz.Workers);
    }

    /// <summary>A workload-only deployment can explicitly opt out of the default request listeners.</summary>
    [Fact]
    public void ShouldCreateNoRequestWorkersGivenExplicitDisableWhenWorkersAreResolved()
    {
        var services = new ServiceCollection();
        var application = services.AddPortia();
        _ = application.AddGeneratedHandler(new RequestRegistration<FitzHostedRequest, FitzHostedHandler>());
        _ = application.AddGeneratedRequest(Transport<FitzHostedRequest>(RequestTransports.Queuable));
        var fitz = new PortiaFitzBuilder(application);

        _ = fitz.DisableRequestWorkers();

        Assert.Empty(fitz.Workers);
    }

    /// <summary>All declared transports are activated once while outbound-only requests are excluded.</summary>
    [Fact]
    public void ShouldCreateDeclaredWorkersOnceGivenSelectedHandlersWhenRequestWorkersAreAdded()
    {
        var services = new ServiceCollection();
        var application = services.AddPortia();
        _ = application.AddGeneratedHandler(new RequestRegistration<FitzHostedRequest, FitzHostedHandler>());
        _ = application.AddGeneratedRequest(Transport<FitzHostedRequest>(RequestTransports.Callable
                                                                         | RequestTransports.Queuable |
                                                                         RequestTransports.Notifiable |
                                                                         RequestTransports.Schedulable));
        _ = application.AddGeneratedRequest(Transport<FitzOutboundOnlyRequest>(RequestTransports.Queuable));
        var fitz = new PortiaFitzBuilder(application);

        _ = fitz.AddRequestWorkers().AddRequestWorkers();

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
        var services = new ServiceCollection();
        var application = services.AddPortia();
        var fitz = new PortiaFitzBuilder(application);
        _ = fitz.AddRequestWorkers();

        _ = application.AddGeneratedHandler(new RequestRegistration<FitzHostedRequest, FitzHostedHandler>());
        _ = application.AddGeneratedRequest(Transport<FitzHostedRequest>(RequestTransports.Queuable));

        Assert.Equal([new FitzQueueWorkerDefinition("queue://app/accounts/*")], fitz.Workers);
    }

    /// <summary>A deployment can activate one transport kind without repeating request types.</summary>
    [Fact]
    public void ShouldCreateOnlyQueueWorkersGivenMultiTransportHandlerWhenQueueWorkersAreAdded()
    {
        var services = new ServiceCollection();
        var application = services.AddPortia();
        _ = application.AddGeneratedHandler(new RequestRegistration<FitzHostedRequest, FitzHostedHandler>());
        _ = application.AddGeneratedRequest(
            Transport<FitzHostedRequest>(RequestTransports.Callable | RequestTransports.Queuable));
        var fitz = new PortiaFitzBuilder(application);

        _ = fitz.AddQueueWorkers();

        Assert.Equal([new FitzQueueWorkerDefinition("queue://app/accounts/*")], fitz.Workers);
    }

    /// <summary>Transport-specific selectors compose after the first one narrows the default.</summary>
    [Fact]
    public void ShouldCreateSelectedWorkerUnionGivenMultipleTransportSelectorsWhenWorkersAreResolved()
    {
        var services = new ServiceCollection();
        var application = services.AddPortia();
        _ = application.AddGeneratedHandler(new RequestRegistration<FitzHostedRequest, FitzHostedHandler>());
        _ = application.AddGeneratedRequest(Transport<FitzHostedRequest>(RequestTransports.Callable
                                                                         | RequestTransports.Queuable |
                                                                         RequestTransports.Notifiable));
        var fitz = new PortiaFitzBuilder(application);

        _ = fitz.AddQueueWorkers().AddNoticeWorkers();

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
        var services = new ServiceCollection();
        var application = services.AddPortia();
        _ = application.AddGeneratedHandler(new RequestRegistration<FitzHostedRequest, FitzHostedHandler>());
        _ = application.AddGeneratedRequest(Transport<FitzHostedRequest>(RequestTransports.Queuable));
        var fitz = new PortiaFitzBuilder(application);
        _ = fitz.AddQueueWorkers();

        var first = fitz.Workers;
        _ = application.AddGeneratedHandler(
            new RequestRegistration<FitzOutboundOnlyRequest, FitzOutboundOnlyHandler>());
        _ = application.AddGeneratedRequest(Transport<FitzOutboundOnlyRequest>(RequestTransports.Queuable));

        Assert.Same(first, fitz.Workers);
        Assert.Equal([new FitzQueueWorkerDefinition("queue://app/accounts/*")], fitz.Workers);
    }

    /// <summary>The RPC server must not expose inferred requests that have no selected local handler.</summary>
    [Fact]
    public async Task ShouldExcludeOutboundRequestsGivenNoSelectedHandlerWhenRpcWorkersAreRegistered()
    {
        var calls = 0;
        var services = new ServiceCollection();
        _ = services.AddSingleton<RequestHandlerRegistration>(
            new RequestRegistration<FitzHostedRequest, FitzHostedHandler>());
        _ = services.AddSingleton<FitzHostedHandler>();
        _ = services.AddSingleton(new RequestTransportRegistration(typeof(FitzHostedRequest),
            RequestTransports.Callable,
            new RequestRouteAttribute("app", "accounts", "*", "hosted"), new DiscriminatorAttribute("test.fitz.hosted"),
            Register));
        _ = services.AddSingleton(new RequestTransportRegistration(typeof(FitzOutboundCallableRequest),
            RequestTransports.Callable,
            new RequestRouteAttribute("app", "accounts", "*", "outbound"),
            new DiscriminatorAttribute("test.fitz.outbound-callable"), Register));
        using var provider = services.BuildServiceProvider();
        var server =
            new FitzRpcRequestServer(new InMemoryRpcClient(), provider.GetRequiredService<IServiceScopeFactory>());

        await using var workers = await server.RegisterRequestsAsync();

        Assert.Equal(1, calls);
        return;

        ValueTask<IAsyncDisposable> Register(IRequestRpcRegistrar registrar, CancellationToken ct)
        {
            calls++;
            return ValueTask.FromResult<IAsyncDisposable>(new NoopAsyncDisposable());
        }
    }

    static RequestTransportRegistration Transport<TRequest>(RequestTransports transports)
        where TRequest : IRequestBase =>
        new(typeof(TRequest), transports, new RequestRouteAttribute("app", "accounts", "*", "run"),
            new DiscriminatorAttribute("test.fitz." + typeof(TRequest).Name));
}
