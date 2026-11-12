using Cntryl.Portia.Testing;

namespace Cntryl.Portia;

/// <summary>Verifies structural comparisons for event payloads in aggregate scenarios.</summary>
public sealed class EventAssertTests
{
    /// <summary>Metadata does not affect recursive comparison of ordered business data.</summary>
    [Fact]
    public void ShouldCompareCollectionsAndNestedRecordsIgnoringMetadata()
    {
        // Arrange
        var expected = new CriteriaRevised(["one", "two"], new RevisionDetails(["a", "b"]));
        var actual = DomainEventSeed.Attach(
            new CriteriaRevised(["one", "two"], new RevisionDetails(["a", "b"])),
            Uuid.CreateVersion4(), 1);

        // Act
        var single = Record.Exception(() => EventAssert.Equal(expected, actual));
        var sequence = Record.Exception(() => EventAssert.Equal([expected], [actual]));

        // Assert
        Assert.Null(single);
        Assert.Null(sequence);
    }

    /// <summary>A mismatch reports the property and collection index for diagnosis.</summary>
    [Fact]
    public void ShouldReportNestedCollectionMismatch()
    {
        // Arrange
        var expected = new CriteriaRevised(["one"], new RevisionDetails(["a", "b"]));

        // Act
        var actual = new CriteriaRevised(["one"], new RevisionDetails(["a", "c"]));

        // Assert
        var error = Assert.Throws<InvalidOperationException>(() => EventAssert.Equal(expected, actual));

        Assert.Contains("Details.Tags[1]", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Different event types and event counts fail the assertion.</summary>
    [Fact]
    public void ShouldRejectDifferentEventTypesAndSequenceLengths()
    {
        // Arrange
        var revised = new CriteriaRevised(["one"], new RevisionDetails(["a"]));

        // Act
        var differentType = Record.Exception(() => EventAssert.Equal(revised, new ValueChanged(1)));
        var differentLength = Record.Exception(() => EventAssert.Equal([revised], Array.Empty<DomainEvent>()));

        // Assert
        _ = Assert.IsType<InvalidOperationException>(differentType);
        _ = Assert.IsType<InvalidOperationException>(differentLength);
    }

    /// <summary>A sequence mismatch names the event index and payload path.</summary>
    [Fact]
    public void ShouldReportEventIndexForSequenceMismatch()
    {
        // Arrange
        var first = new CriteriaRevised(["one"], new RevisionDetails(["a"]));
        var expected = new CriteriaRevised(["two"], new RevisionDetails(["b"]));

        // Act
        var actual = new CriteriaRevised(["two"], new RevisionDetails(["c"]));

        // Assert
        var error = Assert.Throws<InvalidOperationException>(() => EventAssert.Equal([first, expected], [first, actual]));

        Assert.Contains("[1].CriteriaRevised.Details.Tags[0]", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Nested record structs use the same structural collection comparison.</summary>
    [Fact]
    public void ShouldCompareNestedRecordStructCollections()
    {
        // Arrange
        var expected = new StructuredRevision(new RevisionStruct(["a", "b"]));
        var actual = new StructuredRevision(new RevisionStruct(["a", "b"]));

        // Act
        var exception = Record.Exception(() => EventAssert.Equal(expected, actual));

        // Assert
        Assert.Null(exception);
    }

    /// <summary>Fields participate alongside properties at both event and nested payload levels.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShouldReportPublicFieldMismatch(bool nested)
    {
        // Arrange
        DomainEvent expected = nested ? new NestedFields(new FieldValue { Extra = 2 }) : new MixedFields(1) { Extra = 2 };

        // Act
        DomainEvent actual = nested ? new NestedFields(new FieldValue { Extra = 3 }) : new MixedFields(1) { Extra = 3 };

        // Assert
        var error = Assert.Throws<InvalidOperationException>(() => EventAssert.Equal(expected, actual));
        Assert.Contains(nested ? "Details.Extra" : "MixedFields.Extra", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Static state, indexers and non-public getters are outside the payload-member contract.</summary>
    [Fact]
    public void ShouldIgnoreStaticMembersIndexersAndNonPublicGetters()
    {
        // Arrange
        var mixed = new MixedFields(1) { Extra = 2 };
        var otherMixed = new MixedFields(1) { Extra = 2 };
        var nested = new NestedFields(new FieldValue { Extra = 2 });
        var otherNested = new NestedFields(new FieldValue { Extra = 2 });

        // Act
        var mixedException = Record.Exception(() => EventAssert.Equal(mixed, otherMixed));
        var nestedException = Record.Exception(() => EventAssert.Equal(nested, otherNested));

        // Assert
        Assert.Null(mixedException);
        Assert.Null(nestedException);
    }

    sealed record MixedFields(int Id) : DomainEvent
    {
        public int Extra;
        public static int StaticValue => throw new InvalidOperationException("Static member read.");
        public int this[int index] => throw new InvalidOperationException("Indexer read.");
    }

    sealed class FieldValue
    {
        public int Extra;
        public int HiddenRead { private get => throw new InvalidOperationException("Non-public getter read."); set => Extra = value; }
    }

    sealed record NestedFields(FieldValue Details) : DomainEvent;

    sealed record RevisionDetails(IReadOnlyList<string> Tags);

    [Discriminator("test.scenario.criteria-revised")]
    sealed record CriteriaRevised(IReadOnlyList<string> Criteria, RevisionDetails Details) : DomainEvent;

    readonly record struct RevisionStruct(IReadOnlyList<string> Tags);

    [Discriminator("test.scenario.structured-revision")]
    sealed record StructuredRevision(RevisionStruct Details) : DomainEvent;
}
