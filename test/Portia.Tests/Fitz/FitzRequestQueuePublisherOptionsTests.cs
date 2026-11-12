using Cntryl.Fitz;

namespace Cntryl.Portia;

/// <summary>Qualifies native queue publication controls without changing request identity or legacy publication.</summary>
public sealed class FitzRequestQueuePublisherOptionsTests
{
    /// <summary>Publication controls reach Fitz while the request, credential, and logical metadata remain intact.</summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(3, 2)]
    [InlineData(4, null)]
    public async Task ShouldForwardNativeDelayAndLifetimeWithoutChangingEnvelope(int seconds, int? hours)
    {
        // Arrange
        var queue = new PublicationQueue();
        var serializer = TestJson.Serializer(typeof(UniversalAction));
        IRequestQueuePublisher publisher = new FitzRequestQueuePublisher(queue, serializer);
        var request = new UniversalAction(42);
        var metadata = new RequestMetadata(Uuid.CreateVersion4(), Uuid.CreateVersion4(), Uuid.CreateVersion4());
        var options = new RequestQueueOptions(TimeSpan.FromSeconds(seconds), hours is { } value ? (uint)value : null);

        // Act
        await publisher.EnqueueWithOptionsAsync(request, RequestRouteValues.None, "actor-token", metadata, options, default);

        // Assert
        Assert.Equal("queue://test/shared/action", queue.Route);
        Assert.Equal(new QueueEnqueueOptions(options.Delay, options.TtlHours), queue.Options);
        Assert.Equal(1, queue.Accepted);
        Assert.Equal(0, queue.LegacyCalls);
        var envelope = serializer.DeserializeEnvelope(queue.Body);
        Assert.Equal(request, envelope.Request);
        Assert.Equal(metadata, envelope.Metadata);
        Assert.Equal("actor-token", envelope.ActorToken);
    }

    /// <summary>The options convenience method retains an explicitly identified operation.</summary>
    [Fact]
    public async Task ShouldPreserveTypedOperationIdentityWhenPublishingWithOptions()
    {
        // Arrange
        var queue = new PublicationQueue();
        var serializer = TestJson.Serializer(typeof(RequestIdentityTests.IdentifiedCommand));
        IRequestQueuePublisher publisher = new FitzRequestQueuePublisher(queue, serializer);
        var request = new RequestIdentityTests.IdentifiedCommand(Uuid.CreateVersion4());

        // Act
        await publisher.EnqueueWithOptionsAsync(request, RequestRouteValues.None, "actor-token",
            new RequestQueueOptions(TtlHours: 1));

        // Assert
        Assert.Equal(request.CommandId, serializer.DeserializeEnvelope(queue.Body).Metadata.RequestId);
    }

    /// <summary>The parent-context helper preserves correlation and causation through the options path.</summary>
    [Fact]
    public async Task ShouldPreserveParentContextWhenPublishingWithOptions()
    {
        // Arrange
        var queue = new PublicationQueue();
        var serializer = TestJson.Serializer(typeof(UniversalAction));
        IRequestQueuePublisher publisher = new FitzRequestQueuePublisher(queue, serializer);
        var parent = new RequestContext<UniversalAction>(new UniversalAction(1));

        await publisher.EnqueueWithOptionsAsync(new UniversalAction(2), RequestRouteValues.None, "actor-token", parent,
            new RequestQueueOptions(TimeSpan.FromSeconds(2), 1));

        // Act
        var metadata = serializer.DeserializeEnvelope(queue.Body).Metadata;

        // Assert
        Assert.Equal(parent.CorrelationId, metadata.CorrelationId);
        Assert.Equal(parent.CauseId, metadata.CausationId);
        Assert.NotEqual(parent.RequestId, metadata.RequestId);
    }

    /// <summary>Invalid durations and lifetimes fail before the provider can enqueue anything.</summary>
    [Theory]
    [InlineData(-1000, 1)]
    [InlineData(1, 1)]
    [InlineData(0, 0)]
    public async Task ShouldRejectInvalidOptionsBeforeTouchingQueue(int milliseconds, int hours)
    {
        // Arrange
        var queue = new PublicationQueue();

        // Act
        IRequestQueuePublisher publisher = new FitzRequestQueuePublisher(queue, TestJson.Serializer(typeof(UniversalAction)));

        // Assert
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await publisher.EnqueueWithOptionsAsync(new UniversalAction(1), RequestRouteValues.None, null,
                new RequestQueueOptions(TimeSpan.FromMilliseconds(milliseconds), (uint)hours)));

        Assert.Equal(0, queue.OptionCalls);
        Assert.Equal(0, queue.LegacyCalls);
    }

    /// <summary>Cancellation before publication cannot reach either provider enqueue operation.</summary>
    [Fact]
    public async Task ShouldLeavePublicationUndispatchedWhenAlreadyCancelled()
    {
        // Arrange
        var queue = new PublicationQueue();
        IRequestQueuePublisher publisher = new FitzRequestQueuePublisher(queue, TestJson.Serializer(typeof(UniversalAction)));
        using var cancellation = new CancellationTokenSource();

        // Act
        cancellation.Cancel();

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await publisher.EnqueueWithOptionsAsync(new UniversalAction(1), RequestRouteValues.None, null,
                new RequestQueueOptions(TtlHours: 1), cancellation.Token));

        Assert.Equal(0, queue.OptionCalls);
        Assert.Equal(0, queue.LegacyCalls);
    }

    /// <summary>Unavailable requested capability does not fall back to an enqueue without the requested lifetime.</summary>
    [Fact]
    public async Task ShouldPropagateUnavailableNativeCapabilityWithoutPublicationFallback()
    {
        // Arrange
        var queue = new PublicationQueue { SupportsLifetime = false };

        // Act
        IRequestQueuePublisher publisher = new FitzRequestQueuePublisher(queue, TestJson.Serializer(typeof(UniversalAction)));

        // Assert
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await publisher.EnqueueWithOptionsAsync(new UniversalAction(1), RequestRouteValues.None, null,
                new RequestQueueOptions(TtlHours: 1)));

        Assert.Equal(1, queue.OptionCalls);
        Assert.Equal(0, queue.Accepted);
        Assert.Equal(0, queue.LegacyCalls);
    }

    /// <summary>Existing enqueue calls retain their provider-default publication path.</summary>
    [Fact]
    public async Task ShouldKeepLegacyEnqueueOnExistingNativePath()
    {
        // Arrange
        var queue = new PublicationQueue();
        IRequestQueuePublisher publisher = new FitzRequestQueuePublisher(queue, TestJson.Serializer(typeof(UniversalAction)));

        // Act
        await publisher.EnqueueAsync(new UniversalAction(1), RequestRouteValues.None, null);

        // Assert
        Assert.Equal(1, queue.LegacyCalls);
        Assert.Equal(0, queue.OptionCalls);
        Assert.Equal(1, queue.Accepted);
    }

    /// <summary>Existing custom publishers remain compatible when no additional publication semantics are requested.</summary>
    [Fact]
    public async Task ShouldAllowDefaultOptionsOnLegacyPublisherWithoutDroppingLogicalMetadata()
    {
        // Arrange
        var implementation = new LegacyPublisher();
        IRequestQueuePublisher publisher = implementation;
        var metadata = RequestMetadata.Create();

        // Act
        await publisher.EnqueueWithOptionsAsync(new UniversalAction(1), RequestRouteValues.None, null, metadata,
            new RequestQueueOptions(TimeSpan.Zero), default);

        // Assert
        Assert.Equal(1, implementation.Calls);
        Assert.Equal(metadata, implementation.Metadata);
    }

    /// <summary>Default interface implementations reject nondefault semantics rather than ignoring them.</summary>
    [Theory]
    [InlineData(1, null)]
    [InlineData(0, 1)]
    public async Task ShouldRejectNondefaultOptionsOnLegacyPublisher(int seconds, int? hours)
    {
        // Arrange
        var implementation = new LegacyPublisher();

        // Act
        IRequestQueuePublisher publisher = implementation;

        // Assert
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await publisher.EnqueueWithOptionsAsync(new UniversalAction(1), RequestRouteValues.None, null,
                new RequestQueueOptions(TimeSpan.FromSeconds(seconds), hours is { } value ? (uint)value : null)));

        Assert.Equal(0, implementation.Calls);
    }

    sealed class LegacyPublisher : IRequestQueuePublisher
    {
        internal int Calls { get; private set; }
        internal RequestMetadata? Metadata { get; private set; }

        public ValueTask EnqueueAsync<TRequest>(TRequest request, RequestRouteValues routeValues, string? actorToken,
            RequestMetadata metadata, CancellationToken ct = default) where TRequest : IRequest, IQueuable
        {
            Calls++;
            Metadata = metadata;
            return ValueTask.CompletedTask;
        }
    }

    sealed class PublicationQueue : IQueueClient
    {
        internal bool SupportsLifetime { get; init; } = true;
        internal int LegacyCalls { get; private set; }
        internal int OptionCalls { get; private set; }
        internal int Accepted { get; private set; }
        internal string? Route { get; private set; }
        internal ReadOnlyMemory<byte> Body { get; private set; }
        internal QueueEnqueueOptions? Options { get; private set; }

        public Task<ulong> EnqueueAsync(string route, ReadOnlyMemory<byte> body, TimeSpan? delay = null,
            CancellationToken ct = default)
        {
            LegacyCalls++;
            return Accept(route, body, new QueueEnqueueOptions(delay));
        }

        public Task<ulong> EnqueueWithOptionsAsync(string route, ReadOnlyMemory<byte> body, QueueEnqueueOptions options,
            CancellationToken ct = default)
        {
            OptionCalls++;
            if (options.TtlHours is not null && !SupportsLifetime)
                return Task.FromException<ulong>(new NotSupportedException("Queue TTL capability was not negotiated."));
            return Accept(route, body, options);
        }

        Task<ulong> Accept(string route, ReadOnlyMemory<byte> body, QueueEnqueueOptions options)
        {
            Route = route;
            Body = body;
            Options = options;
            Accepted++;
            return Task.FromResult(1UL);
        }

        public Task<IQueueReservedItem[]> ReserveAsync(string route, TimeSpan lease, int batchSize = 1,
            TimeSpan? wait = null, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<QueueSubscription> SubscribeAsync(string pattern, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
