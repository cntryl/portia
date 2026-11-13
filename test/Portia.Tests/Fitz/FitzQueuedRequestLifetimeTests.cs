using Cntryl.Fitz;

namespace Cntryl.Portia;

/// <summary>Covers reading a delivered Fitz queue request after its reservation was released.</summary>
[Collection(FitzBrokerCollectionDefinition.Name)]
[Trait("Category", "BrokerIntegration")]
public sealed class FitzQueuedRequestLifetimeTests(FitzBrokerFixture broker)
{
    /// <summary>The loss signal stays readable once the delivery's reservation resources are released.</summary>
    [Fact]
    public async Task ShouldExposeReservationCancellationGivenDeliveryReleased()
    {
        // Arrange
        await using var connection = await broker.CreateClientAsync();
        var resource = "lifetime-" + Uuid.CreateVersion4();
        var route = $"queue://test/lifetime/{resource}";
        var catalog = new RequestTransportCatalog([
            new RequestTransportRegistration(typeof(UniversalAction), [RequestTransportId.Queue],
                new RequestRouteAttribute("test", "lifetime", resource, "run"),
                new DiscriminatorAttribute("test.shared.universal-action"))
        ]);
        var serializer = TestJson.Serializer(typeof(UniversalAction));
        IRequestQueuePublisher publisher = new FitzRequestQueuePublisher(connection.Queue, serializer, catalog);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var consumer = new FitzRequestQueueConsumer(connection.Queue, serializer, route, catalog,
            visibilityTimeoutSeconds: 5, waitDuration: TimeSpan.FromMilliseconds(100));
        var reader = consumer.ReadAsync(deadline.Token).GetAsyncEnumerator(deadline.Token);
        await publisher.EnqueueAsync(new UniversalAction(1), RequestRouteValues.None, null, ct: deadline.Token);
        _ = await reader.MoveNextAsync();
        var delivered = reader.Current;
        await reader.DisposeAsync();

        // Act
        var failure = Record.Exception(() => delivered.ReservationCancellation);

        // Assert
        Assert.Null(failure);
    }
}
