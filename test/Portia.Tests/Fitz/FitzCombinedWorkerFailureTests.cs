namespace Cntryl.Portia;

/// <summary>Checks terminal queue failure through the combined Fitz background-service lifetime.</summary>
[Collection(FitzBrokerCollectionDefinition.Name)]
[Trait("Category", "BrokerIntegration")]
public sealed class FitzCombinedWorkerFailureTests(FitzBrokerFixture broker)
{
    /// <summary>A healthy sibling listener must not hide a terminal observer failure from the host.</summary>
    /// <param name="withHealthyNoticeListener">Whether another listener remains healthy and pending.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldFaultCombinedHostWhenTerminalObserverFails(bool withHealthyNoticeListener)
    {
        await using var client = await broker.CreateClientAsync();
        var resource = "pass2-host-fatal-" + Uuid.CreateVersion4();
        var descriptor = new RequestTransportRegistration(typeof(UniversalAction),
            [RequestTransportId.Queue, RequestTransportId.Notice],
            new RequestRouteAttribute("test", "pass2-workers", resource, "run"),
            new DiscriminatorAttribute("test.shared.universal-action"));
        var serializer = new JsonRequestSerializer([descriptor], TestJson.Options());
        var catalog = new RequestTransportCatalog([descriptor]);
        var observer = new ThrowingObserver();
        using var busHost = TestRequestBus.Create();
        var services = new ServiceCollection();
        _ = services.AddSingleton(busHost.Bus);
        _ = services.AddSingleton<IRequestActorValidator>(new TestRequestActorValidator());
        _ = services.AddSingleton<IRequestDeserializer>(serializer);
        _ = services.AddSingleton<IQueuedRequestTerminalHandler>(observer);
        // Supply exact test descriptors without adding compilation-wide inferred outbound routes.
        var application = new PortiaBuilder(services);
        _ = application.AddGeneratedHandler(new RequestRegistration<UniversalAction, UniversalActionHandler>());
        _ = application.AddGeneratedRequest(descriptor);
        var configuration = new PortiaFitzBuilder(application, client);
        _ = configuration.AddQueueWorkers();
        if (withHealthyNoticeListener)
            _ = configuration.AddNoticeWorkers();
        await using var provider = services.BuildServiceProvider();
        await using var connection = new FitzApplicationConnection(client, false, TimeSpan.FromSeconds(5));
        var worker = new FitzApplicationWorkers(connection, configuration,
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IServiceProviderIsService>(), [], catalog, serializer);
        var publisher = new FitzRequestQueuePublisher(client.Queue, serializer, catalog);
        // Missing credentials exercise an ordinary terminal actor-validation failure. The observer's
        // exception must escape to the host, regardless of other listeners running alongside it.
        await publisher.EnqueueAsync(new UniversalAction(7), RequestRouteValues.None, null);

        try
        {
            await worker.StartAsync(default);
            await observer.Seen.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var execution = Assert.IsAssignableFrom<Task>(worker.ExecuteTask);

            _ = await Assert.ThrowsAsync<TerminalHandlerFailureException>(() =>
                execution.WaitAsync(TimeSpan.FromMilliseconds(500)));
        }
        finally
        {
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await worker.StopAsync(shutdown.Token);
            }
            catch (TerminalHandlerFailureException)
            {
            }
            worker.Dispose();
        }
    }

    sealed class ThrowingObserver : IQueuedRequestTerminalHandler
    {
        internal TaskCompletionSource Seen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask HandleAsync(QueuedRequestFailureContext context, CancellationToken ct = default)
        {
            Seen.TrySetResult();
            throw new InvalidOperationException("terminal observation failed");
        }
    }
}
