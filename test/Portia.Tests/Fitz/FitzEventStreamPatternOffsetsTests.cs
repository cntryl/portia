namespace Cntryl.Portia;

/// <summary>
///     Verifies <see cref="FitzEventStreamPatternOffsets" /> — the pattern-scope offset arithmetic
///     extracted out of <see cref="FitzEventStore" /> so that class is left responsible only for
///     stream I/O orchestration, not scope semantics.
/// </summary>
public sealed class FitzEventStreamPatternOffsetsTests
{
    /// <summary>Verifies resource-scoped patterns select the resource offset.</summary>
    [Fact]
    public void ShouldReturnTheResourceOffsetForResourceScope()
    {
        var pattern = EventStreamPattern.ForPattern("realm", "area", "resource");

        var offset = FitzEventStreamPatternOffsets.GetPatternOffset(pattern, 1, 2, 3);

        Assert.Equal(1UL, offset);
    }

    /// <summary>Verifies area-scoped patterns select the area offset.</summary>
    [Fact]
    public void ShouldReturnTheAreaOffsetForAreaScope()
    {
        var pattern = EventStreamPattern.ForPattern("realm", "area");

        var offset = FitzEventStreamPatternOffsets.GetPatternOffset(pattern, 1, 2, 3);

        Assert.Equal(2UL, offset);
    }

    /// <summary>Verifies realm-scoped patterns select the realm offset.</summary>
    [Fact]
    public void ShouldReturnTheRealmOffsetForRealmScope()
    {
        var pattern = EventStreamPattern.ForPattern("realm");

        var offset = FitzEventStreamPatternOffsets.GetPatternOffset(pattern, 1, 2, 3);

        Assert.Equal(3UL, offset);
    }

    /// <summary>Verifies a stream matches a pattern when every non-null segment equals it.</summary>
    [Fact]
    public void ShouldMatchAStreamWhenEveryNonNullPatternSegmentEqualsIt()
    {
        var stream = new EventStreamAddress("realm", "area", "resource");
        var pattern = EventStreamPattern.ForPattern("realm", "area");

        Assert.True(FitzEventStreamPatternOffsets.Matches(stream, pattern));
    }

    /// <summary>Verifies a stream doesn't match a pattern whose segment differs from it.</summary>
    [Fact]
    public void ShouldNotMatchAStreamWhenAPatternSegmentDiffers()
    {
        var stream = new EventStreamAddress("realm", "area", "resource");
        var pattern = EventStreamPattern.ForPattern("realm", "other-area");

        Assert.False(FitzEventStreamPatternOffsets.Matches(stream, pattern));
    }
}
