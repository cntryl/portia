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
        var ev = new StubEvent();
        ev.AttachMetadata(new DomainEventMetadata(
            Uuid.CreateVersion4(),
            Uuid.CreateVersion4(),
            1,
            DateTimeOffset.UtcNow));

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
        var ev = new StubEvent();

        _ = Assert.Throws<InvalidOperationException>(() => ev.Metadata);
    }

    sealed record StubEvent : DomainEvent;
}
