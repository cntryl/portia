using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia.Consumer;

[Trait("Category", "BrokerIntegration")]
public sealed class QueueBrokerTests
{
    [Fact]
    public async Task ShouldRedeliverMalformedReservationGivenValidWorkAcknowledged()
    {
        // Arrange
        await using var client = await ConsumerBroker.ConnectAsync();
        var route = "queue://portia-consumer-tests/queue/" + Guid.NewGuid().ToString("N");
        var serializer = ConsumerJson.CreateSerializer();
        _ = await client.Queue.EnqueueAsync(route, "{"u8.ToArray(), ct: TestContext.Current.CancellationToken);
        var id = Uuid.CreateVersion4();
        _ = await client.Queue.EnqueueAsync(route,
            serializer.Serialize(new ScopeRequest(id), null, RequestMetadata.Create(), null), ct: TestContext.Current.CancellationToken);
        var consumer = new FitzRequestQueueConsumer(client.Queue, serializer, route, ConsumerJson.Catalog());

        // Act
        await using var reader = consumer.ReadAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);

        // Assert
        Assert.True(await reader.MoveNextAsync());
        var malformed = reader.Current;
        _ = Assert.ThrowsAny<JsonException>(() => malformed.Request);
        await malformed.RetryAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(id, Assert.IsType<ScopeRequest>(reader.Current.Request).Id);
        await reader.Current.CompleteAsync(TestContext.Current.CancellationToken);
        var redelivered = Assert.Single(await client.Queue.ReserveAsync(
            route, TimeSpan.FromSeconds(30), wait: TimeSpan.FromSeconds(3), ct: TestContext.Current.CancellationToken));
        Assert.Equal("{"u8.ToArray(), redelivered.Body.ToArray());
        // Test cleanup acknowledges the observed redelivery; Portia never acknowledged the malformed delivery.
        await redelivered.CompleteAsync(TestContext.Current.CancellationToken);
        Assert.Empty(await client.Queue.ReserveAsync(route, TimeSpan.FromSeconds(1), wait: TimeSpan.Zero, ct: TestContext.Current.CancellationToken));
    }

    /// <summary>
    ///     A Fitz reservation reports the wire name its envelope declared, so queue-side telemetry
    ///     stays pinned to the discriminator rather than falling back to the CLR type name.
    /// </summary>
    [Fact]
    public async Task ShouldReportEnvelopeDiscriminatorAsWireNameGivenQueuedRequest()
    {
        // Arrange
        await using var client = await ConsumerBroker.ConnectAsync();
        var route = "queue://portia-consumer-tests/queue/" + Guid.NewGuid().ToString("N");
        var serializer = ConsumerJson.CreateSerializer();
        _ = await client.Queue.EnqueueAsync(route,
            serializer.Serialize(new ScopeRequest(Uuid.CreateVersion4()), null, RequestMetadata.Create(), null), ct: TestContext.Current.CancellationToken);
        var consumer = new FitzRequestQueueConsumer(client.Queue, serializer, route, ConsumerJson.Catalog());

        // Act
        await using var reader = consumer.ReadAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);

        // Assert
        Assert.True(await reader.MoveNextAsync());

        Assert.Equal("consumer.scopes.scope-request", reader.Current.Name);
        Assert.NotEqual(nameof(ScopeRequest), reader.Current.Name);
        await reader.Current.CompleteAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ShouldRetainLeaseAndHandBackRedeliveryGivenActiveDeliveryWhenCancelled()
    {
        // Arrange
        await using var client = await ConsumerBroker.ConnectAsync();
        await using var competitor = await ConsumerBroker.ConnectAsync();
        var route = "queue://portia-consumer-tests/queue/" + Guid.NewGuid().ToString("N");
        var serializer = ConsumerJson.CreateSerializer();
        var id = Uuid.CreateVersion4();
        _ = await client.Queue.EnqueueAsync(route,
            serializer.Serialize(new ScopeRequest(id, 2), null, RequestMetadata.Create(), null), ct: TestContext.Current.CancellationToken);
        var services = ConsumerHost.CreateServices();
        _ = services.AddAccounts();
        _ = services.AddScoped<IRequestActorValidator, DeliveryScopeTests.ScopeValidator>();
        // Leave enough headroom for the first renewal on slower shared CI runners. The competing
        // reserve waits beyond the original lease, so this still proves renewal rather than merely
        // observing the initial reservation window.
        _ = services.AddSingleton<IRequestQueueConsumer>(new FitzRequestQueueConsumer(client.Queue, serializer, route,
            ConsumerJson.Catalog(), 3));
        _ = services.AddPortiaQueueRunner();
        await using var provider = ConsumerHost.Build(services);
        using var cancellation = new CancellationTokenSource();

        // Act
        var run = provider.GetRequiredService<QueueRunner>().RunAsync(cancellation.Token);

        // Assert
        try
        {
            await provider.GetRequiredService<ConsumerHost.Effects>().WaitForAsync("nested");
            Assert.Empty(await competitor.Queue.ReserveAsync(
                route, TimeSpan.FromSeconds(1), wait: TimeSpan.FromSeconds(4), ct: TestContext.Current.CancellationToken));
        }
        finally
        {
            cancellation.Cancel();

            try
            {
                await run;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
        }

        var redelivered = Assert.Single(await competitor.Queue.ReserveAsync(
            route, TimeSpan.FromSeconds(30), wait: TimeSpan.FromSeconds(3), ct: TestContext.Current.CancellationToken));
        var request = serializer.DeserializeEnvelope(redelivered.Body).Request;
        Assert.Equal(id, Assert.IsType<ScopeRequest>(request).Id);
        await redelivered.CompleteAsync(TestContext.Current.CancellationToken);
        Assert.All(provider.GetRequiredService<ConsumerHost.Effects>().Scopes.Values, Assert.True);
    }
}
