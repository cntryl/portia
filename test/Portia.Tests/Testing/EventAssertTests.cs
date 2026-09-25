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

    sealed record RevisionDetails(IReadOnlyList<string> Tags);

    [Discriminator("test.scenario.criteria-revised")]
    sealed record CriteriaRevised(IReadOnlyList<string> Criteria, RevisionDetails Details) : DomainEvent;
}
