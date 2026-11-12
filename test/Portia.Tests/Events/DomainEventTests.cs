namespace Cntryl.Portia;

/// <summary>
///     Verifies domain event metadata attachment semantics.
/// </summary>
public sealed class DomainEventTests
{
    /// <summary>
    ///     Verifies that an event's metadata cannot be reassigned once attached.
    /// </summary>
    [Fact]
    public void ShouldThrowWhenMetadataAttachedMoreThanOnce()
    {
        // Arrange
        var ev = new StubEvent();

        // Act
        ev.AttachMetadata(new DomainEventMetadata(
            Uuid.CreateVersion4(),
            Uuid.CreateVersion4(),
            1,
            DateTimeOffset.UtcNow));

        // Assert
        _ = Assert.Throws<InvalidOperationException>(() => ev.AttachMetadata(new DomainEventMetadata(
            Uuid.CreateVersion4(),
            Uuid.CreateVersion4(),
            1,
            DateTimeOffset.UtcNow)));
    }

    /// <summary>
    ///     Verifies that reading metadata before it is attached fails clearly.
    /// </summary>
    [Fact]
    public void ShouldThrowWhenMetadataReadBeforeAttached()
    {
        // Arrange
        var ev = new StubEvent();

        // Act
        var exception = Record.Exception(() => ev.Metadata);

        // Assert
        _ = Assert.IsType<InvalidOperationException>(exception);
    }

    /// <summary>
    ///     Verifies that equality compares business payload only, so a raised event equals a freshly
    ///     constructed expectation regardless of the metadata Portia attached.
    /// </summary>
    [Fact]
    public void ShouldCompareEventsByPayloadIgnoringMetadata()
    {
        // Arrange
        var attached = new ValueChanged(3);

        // Act
        attached.AttachMetadata(new DomainEventMetadata(
            Uuid.CreateVersion4(),
            Uuid.CreateVersion4(),
            1,
            DateTimeOffset.UtcNow));

        // Assert
        Assert.Equal(new ValueChanged(3), attached);
        Assert.Equal(new ValueChanged(3).GetHashCode(), attached.GetHashCode());
        Assert.NotEqual(new ValueChanged(4), attached);
        Assert.NotEqual<DomainEvent>(new ValueIncremented(3), attached);
    }

    /// <summary>
    ///     Verifies that a <c>with</c> copy is a new event: it starts without the original's metadata,
    ///     so it can never share an event ID with the event it was copied from.
    /// </summary>
    [Fact]
    public void ShouldStartCopiedEventWithoutMetadata()
    {
        // Arrange
        var attached = new ValueChanged(3);
        attached.AttachMetadata(new DomainEventMetadata(
            Uuid.CreateVersion4(),
            Uuid.CreateVersion4(),
            1,
            DateTimeOffset.UtcNow));

        // Act
        var copy = attached with { Value = 4 };

        // Assert
        _ = Assert.Throws<InvalidOperationException>(() => copy.Metadata);
        copy.AttachMetadata(new DomainEventMetadata(
            Uuid.CreateVersion4(),
            Uuid.CreateVersion4(),
            1,
            DateTimeOffset.UtcNow));
        Assert.NotEqual(attached.Metadata.EventId, copy.Metadata.EventId);
    }

    sealed record StubEvent : DomainEvent;
}
