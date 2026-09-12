
namespace Cntryl.Portia;

/// <summary>
///     Verifies how a paged Fitz read resumes. A projector checkpoints against whichever offset its
///     pattern's scope selects, so reading the wrong one out of a page cursor either replays events the
///     component already handled or skips past ones it never saw — neither of which the component can
///     detect afterwards. The area scope was the only one previously exercised; realm and resource
///     reads resume from different cursor fields entirely.
/// </summary>
public sealed class FitzEventStreamPatternCursorTests
{
    /// <summary>
    ///     Verifies that each scope resumes one past the last offset of its own kind, never another
    ///     scope's — the three counters advance independently, so mixing them silently misplaces a
    ///     checkpoint.
    /// </summary>
    /// <param name="area">The area segment, or <see langword="null" /> for a realm-scoped pattern.</param>
    /// <param name="resource">The resource segment, or <see langword="null" /> for a wider pattern.</param>
    /// <param name="expected">The offset the next read must start from.</param>
    [Theory]
    [InlineData("orders", "order-1", 11UL)]
    [InlineData("orders", null, 21UL)]
    [InlineData(null, null, 31UL)]
    public void ShouldResumeFromTheCursorFieldMatchingThePatternScope(string? area, string? resource, ulong expected)
    {
        var pattern = EventStreamPattern.ForPattern("tenant", area, resource);
        var cursor = new StreamReadCursor(10, 20, 30, 40, null, null, false);

        Assert.Equal(expected, FitzEventStreamPatternOffsets.GetNextOffset(pattern, cursor));
    }

    /// <summary>
    ///     Verifies that a broker page which omits the counter the pattern's scope needs fails loudly.
    ///     Treating the missing value as zero would restart the component from the beginning of the
    ///     stream and replay every effect it has already produced.
    /// </summary>
    /// <param name="area">The area segment, or <see langword="null" /> for a realm-scoped pattern.</param>
    /// <param name="expected">The wording that names the missing cursor.</param>
    [Theory]
    [InlineData("orders", "area cursor")]
    [InlineData(null, "realm cursor")]
    public void ShouldFailWhenTheCursorOmitsTheScopesOffset(string? area, string expected)
    {
        var pattern = EventStreamPattern.ForPattern("tenant", area);
        var cursor = new StreamReadCursor(10, null, null, null, null, null, false);

        var error = Assert.Throws<InvalidOperationException>(() =>
            FitzEventStreamPatternOffsets.GetNextOffset(pattern, cursor));

        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Verifies that a resource-scoped read needs no wider counters at all, so a broker that supplies
    ///     only the resource offset still resumes a single-stream read correctly.
    /// </summary>
    [Fact]
    public void ShouldResumeAResourceScopedReadWithoutAnyWiderCursor()
    {
        var pattern = EventStreamPattern.ForPattern("tenant", "orders", "order-1");
        var cursor = new StreamReadCursor(7, null, null, null, null, null, false);

        Assert.Equal(8UL, FitzEventStreamPatternOffsets.GetNextOffset(pattern, cursor));
    }

    /// <summary>
    ///     Verifies the same scope selection for a record's own offsets, and that a record missing the
    ///     offset its scope needs is refused rather than checkpointed against the wrong counter.
    /// </summary>
    /// <param name="area">The area segment, or <see langword="null" /> for a realm-scoped pattern.</param>
    /// <param name="resource">The resource segment, or <see langword="null" /> for a wider pattern.</param>
    /// <param name="expected">The offset the record reports for that scope.</param>
    [Theory]
    [InlineData("orders", "order-1", 10UL)]
    [InlineData("orders", null, 20UL)]
    [InlineData(null, null, 30UL)]
    public void ShouldSelectTheRecordOffsetMatchingThePatternScope(string? area, string? resource, ulong expected)
    {
        var pattern = EventStreamPattern.ForPattern("tenant", area, resource);

        Assert.Equal(expected, FitzEventStreamPatternOffsets.GetPatternOffset(pattern, 10, 20, 30));
    }

    /// <summary>
    ///     Verifies that a record missing the wider offset its scope needs is refused, naming which one
    ///     the broker failed to supply.
    /// </summary>
    /// <param name="area">The area segment, or <see langword="null" /> for a realm-scoped pattern.</param>
    /// <param name="expected">The wording that names the missing offset.</param>
    [Theory]
    [InlineData("orders", "area offset")]
    [InlineData(null, "realm offset")]
    public void ShouldFailWhenARecordOmitsTheScopesOffset(string? area, string expected)
    {
        var pattern = EventStreamPattern.ForPattern("tenant", area);

        var error = Assert.Throws<InvalidOperationException>(() =>
            FitzEventStreamPatternOffsets.GetPatternOffset(pattern, 10, null, null));

        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }
}
