using Cntryl.Fitz;

namespace Cntryl.Portia;

/// <summary>Qualifies Portia publication controls and explicit retry against the native Wire 3 broker.</summary>
[Collection(FitzBrokerCollectionDefinition.Name)]
[Trait("Category", "BrokerIntegration")]
public sealed class FitzQueueOptionsIntegrationTests(FitzBrokerFixture broker)
{
    /// <summary>Initial delay and native NACK delay preserve the original request until successful acknowledgement.</summary>
    [Fact]
    public async Task ShouldPublishDelayedRequestAndRetrySameEnvelopeGivenLifetimeAndNativeNack()
    {
        // Arrange
        await using var connection = await broker.CreateClientAsync();
        Assert.Contains(1U, connection.CapabilityIds);
        Assert.Contains(2U, connection.CapabilityIds);
        var resource = "options-" + Uuid.CreateVersion4();
        var route = $"queue://test/options/{resource}";
        var catalog = new RequestTransportCatalog([
            new RequestTransportRegistration(typeof(UniversalAction), [RequestTransportId.Queue],
                new RequestRouteAttribute("test", "options", resource, "run"),
                new DiscriminatorAttribute("test.shared.universal-action"))
        ]);
        var serializer = TestJson.Serializer(typeof(UniversalAction));
        IRequestQueuePublisher publisher = new FitzRequestQueuePublisher(connection.Queue, serializer, catalog);
        var metadata = new RequestMetadata(Uuid.CreateVersion4(), Uuid.CreateVersion4(), Uuid.CreateVersion4());
        var request = new UniversalAction(174);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var consumer = new FitzRequestQueueConsumer(connection.Queue, serializer, route, catalog,
            visibilityTimeoutSeconds: 5, waitDuration: TimeSpan.FromMilliseconds(100));
        await using var reader = consumer.ReadAsync(deadline.Token).GetAsyncEnumerator(deadline.Token);

        // Act
        await publisher.EnqueueWithOptionsAsync(request, RequestRouteValues.None, "original-actor", metadata,
            new RequestQueueOptions(Delay: TimeSpan.FromSeconds(3), TtlHours: 1), deadline.Token);
        var reservedBeforeDelay = await connection.Queue.ReserveAsync(route, TimeSpan.FromSeconds(5),
            wait: TimeSpan.Zero, ct: deadline.Token);
        var readFirst = await reader.MoveNextAsync();
        var first = reader.Current;

        await first.RetryAsync(TimeSpan.FromSeconds(3), deadline.Token);

        var reservedAfterRetry = await connection.Queue.ReserveAsync(route, TimeSpan.FromSeconds(5),
            wait: TimeSpan.Zero, ct: deadline.Token);
        var readRetried = await reader.MoveNextAsync();
        var retried = reader.Current;
        await retried.CompleteAsync(deadline.Token);

        var reservedAfterComplete = await connection.Queue.ReserveAsync(route, TimeSpan.FromSeconds(5),
            wait: TimeSpan.Zero, ct: deadline.Token);

        // Assert
        Assert.Empty(reservedBeforeDelay);
        Assert.True(readFirst);
        Assert.Equal(request, first.Request);
        Assert.Equal(metadata, first.Metadata);
        Assert.Equal("original-actor", first.ActorToken);
        Assert.Equal(0U, first.Attempt);
        Assert.Empty(reservedAfterRetry);
        Assert.True(readRetried);
        Assert.Equal(request, retried.Request);
        Assert.Equal(metadata, retried.Metadata);
        Assert.Equal(first.ActorToken, retried.ActorToken);
        Assert.Equal(0U, retried.Attempt);
        Assert.Empty(reservedAfterComplete);
    }
}
