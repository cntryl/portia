using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia.Consumer;

public sealed class QueueBrokerTests
{
    [Fact]
    public async Task MalformedReservationIsRedeliveredByFitzAfterValidWorkIsAcknowledged()
    {
        await using var client = await ConsumerBroker.ConnectAsync();
        var route = "queue://portia-consumer-tests/queue/" + Guid.NewGuid().ToString("N");
        var serializer = ConsumerJson.CreateSerializer();
        _ = await client.Queue.EnqueueAsync(route, "{"u8.ToArray());
        var id = Uuid.CreateVersion4();
        _ = await client.Queue.EnqueueAsync(route,
            serializer.Serialize(new ScopeRequest(id), null, RequestMetadata.Create(), null));
        var consumer = new FitzRequestQueueConsumer(client.Queue, serializer, route, 1);
        await using var reader = consumer.ReadAsync().GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        var malformed = reader.Current;
        var attempt = malformed.Attempt;
        _ = Assert.ThrowsAny<JsonException>(() => malformed.Request);
        await malformed.AbandonAsync();
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(id, Assert.IsType<ScopeRequest>(reader.Current.Request).Id);
        await reader.Current.CompleteAsync();
        var redelivered = Assert.Single(await client.Queue.ReserveAsync(route, 1, waitSeconds: 3));
        Assert.Equal("{"u8.ToArray(), redelivered.Body.ToArray());
        // Fitz .NET 0.1.1 supplies 1 for RESERVE responses; Portia preserves that SDK value.
        Assert.Equal(attempt, redelivered.Attempt);
        // Test cleanup acknowledges the observed redelivery; Portia never acknowledged the malformed delivery.
        await redelivered.CompleteAsync();
        Assert.Empty(await client.Queue.ReserveAsync(route, 1, waitSeconds: 0));
    }

    /// <summary>
    ///     A Fitz reservation reports the wire name its envelope declared, so queue-side telemetry
    ///     stays pinned to the discriminator rather than falling back to the CLR type name.
    /// </summary>
    [Fact]
    public async Task ReservationReportsTheEnvelopeDiscriminatorAsItsWireName()
    {
        await using var client = await ConsumerBroker.ConnectAsync();
        var route = "queue://portia-consumer-tests/queue/" + Guid.NewGuid().ToString("N");
        var serializer = ConsumerJson.CreateSerializer();
        _ = await client.Queue.EnqueueAsync(route,
            serializer.Serialize(new ScopeRequest(Uuid.CreateVersion4()), null, RequestMetadata.Create(), null));
        var consumer = new FitzRequestQueueConsumer(client.Queue, serializer, route, 1);
        await using var reader = consumer.ReadAsync().GetAsyncEnumerator();

        Assert.True(await reader.MoveNextAsync());

        Assert.Equal("consumer.scopes.scope-request", reader.Current.Name);
        Assert.NotEqual(nameof(ScopeRequest), reader.Current.Name);
        await reader.Current.CompleteAsync();
    }

    [Fact]
    public async Task ActiveDeliveryRetainsLeaseThenHandsRedeliveryBackToFitzOnCancellation()
    {
        await using var client = await ConsumerBroker.ConnectAsync();
        await using var competitor = await ConsumerBroker.ConnectAsync();
        var route = "queue://portia-consumer-tests/queue/" + Guid.NewGuid().ToString("N");
        var serializer = ConsumerJson.CreateSerializer();
        var id = Uuid.CreateVersion4();
        _ = await client.Queue.EnqueueAsync(route,
            serializer.Serialize(new ScopeRequest(id, 2), null, RequestMetadata.Create(), null));
        var services = ConsumerHost.CreateServices();
        _ = services.AddAccounts();
        _ = services.AddScoped<IRequestActorValidator, DeliveryScopeTests.ScopeValidator>();
        // Leave enough headroom for the first renewal on slower shared CI runners. The competing
        // reserve waits beyond the original lease, so this still proves renewal rather than merely
        // observing the initial reservation window.
        _ = services.AddSingleton<IRequestQueueConsumer>(new FitzRequestQueueConsumer(client.Queue, serializer, route,
            3));
        _ = services.AddPortiaQueueRunner();
        await using var provider = ConsumerHost.Build(services);
        using var cancellation = new CancellationTokenSource();
        var run = provider.GetRequiredService<QueueRunner>().RunAsync(cancellation.Token);
        try
        {
            await provider.GetRequiredService<ConsumerHost.Effects>().WaitForAsync("nested");
            Assert.Empty(await competitor.Queue.ReserveAsync(route, 1, waitSeconds: 4));
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

        var redelivered = Assert.Single(await competitor.Queue.ReserveAsync(route, 1, waitSeconds: 3));
        var request = serializer.DeserializeEnvelope(redelivered.Body).Request;
        Assert.Equal(id, Assert.IsType<ScopeRequest>(request).Id);
        // The pinned SDK does not expose the broker's durable delivery counter.
        Assert.Equal(1U, redelivered.Attempt);
        await redelivered.CompleteAsync();
        Assert.All(provider.GetRequiredService<ConsumerHost.Effects>().Scopes.Values, Assert.True);
    }
}
