using Cntryl.Portia.Testing;

namespace Cntryl.Portia;

/// <summary>Verifies structural comparisons for event payloads in aggregate scenarios.</summary>
public sealed class EventAssertTests
{
    /// <summary>Metadata does not affect recursive comparison of ordered business data.</summary>
    [Fact]
    public void ShouldCompareCollectionsAndNestedRecordsIgnoringMetadata()
    {
        var expected = new CriteriaRevised(["one", "two"], new RevisionDetails(["a", "b"]));
        var actual = DomainEventSeed.Attach(
            new CriteriaRevised(["one", "two"], new RevisionDetails(["a", "b"])),
            Uuid.CreateVersion4(), 1);

        EventAssert.Equal(expected, actual);
        EventAssert.Equal([expected], [actual]);
    }

    /// <summary>A mismatch reports the property and collection index for diagnosis.</summary>
    [Fact]
    public void ShouldReportNestedCollectionMismatch()
    {
        var expected = new CriteriaRevised(["one"], new RevisionDetails(["a", "b"]));
        var actual = new CriteriaRevised(["one"], new RevisionDetails(["a", "c"]));

        var error = Assert.Throws<InvalidOperationException>(() => EventAssert.Equal(expected, actual));

        Assert.Contains("Details.Tags[1]", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Different event types and event counts fail the assertion.</summary>
    [Fact]
    public void ShouldRejectDifferentEventTypesAndSequenceLengths()
    {
        var revised = new CriteriaRevised(["one"], new RevisionDetails(["a"]));

        _ = Assert.Throws<InvalidOperationException>(() => EventAssert.Equal(revised, new ValueChanged(1)));
        _ = Assert.Throws<InvalidOperationException>(() => EventAssert.Equal([revised], Array.Empty<DomainEvent>()));
    }

    /// <summary>A sequence mismatch names the event index and payload path.</summary>
    [Fact]
    public void ShouldReportEventIndexForSequenceMismatch()
    {
        var first = new CriteriaRevised(["one"], new RevisionDetails(["a"]));
        var expected = new CriteriaRevised(["two"], new RevisionDetails(["b"]));
        var actual = new CriteriaRevised(["two"], new RevisionDetails(["c"]));

        var error = Assert.Throws<InvalidOperationException>(() => EventAssert.Equal([first, expected], [first, actual]));

        Assert.Contains("[1].CriteriaRevised.Details.Tags[0]", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Nested record structs use the same structural collection comparison.</summary>
    [Fact]
    public void ShouldCompareNestedRecordStructCollections()
    {
        var expected = new StructuredRevision(new RevisionStruct(["a", "b"]));
        var actual = new StructuredRevision(new RevisionStruct(["a", "b"]));

        EventAssert.Equal(expected, actual);
    }

    sealed record RevisionDetails(IReadOnlyList<string> Tags);

    [Discriminator("test.scenario.criteria-revised")]
    sealed record CriteriaRevised(IReadOnlyList<string> Criteria, RevisionDetails Details) : DomainEvent;

    readonly record struct RevisionStruct(IReadOnlyList<string> Tags);

    [Discriminator("test.scenario.structured-revision")]
    sealed record StructuredRevision(RevisionStruct Details) : DomainEvent;
}
