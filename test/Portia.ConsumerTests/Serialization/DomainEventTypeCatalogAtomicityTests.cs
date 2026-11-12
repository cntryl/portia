namespace Cntryl.Portia.Consumer;

/// <summary>A rejected registration leaves both catalog constraints unchanged.</summary>
public sealed class DomainEventTypeCatalogAtomicityTests
{
    /// <summary>Rejecting a second discriminator for one type does not reserve the rejected discriminator.</summary>
    [Fact]
    public void ShouldPreserveCatalogGivenRejectedDuplicateClrType()
    {
        // Arrange
        var catalog = new DomainEventTypeCatalog().Register<Created>(1, "original");

        // Act
        var error = Record.Exception(() => catalog.Register<Created>(2, "rejected"));
        var described = catalog.Describe(typeof(Created));
        var reserved = catalog.TryResolve("rejected", 2, out _);
        catalog.Register<Other>(2, "rejected");
        var resolved = catalog.TryResolve("rejected", 2, out var actual);

        // Assert
        _ = Assert.IsType<InvalidOperationException>(error);
        Assert.Equal(("original", 1), described);
        Assert.False(reserved);
        Assert.True(resolved);
        Assert.Equal(typeof(Other), actual);
    }

    /// <summary>Rejecting an occupied discriminator leaves the new CLR type available under another discriminator.</summary>
    [Fact]
    public void ShouldPreserveCatalogGivenRejectedDuplicateDiscriminator()
    {
        // Arrange
        var catalog = new DomainEventTypeCatalog().Register<Created>(1, "original");

        // Act
        var rejection = Record.Exception(() => catalog.Register<Other>(1, "original"));
        var originalResolved = catalog.TryResolve("original", 1, out var original);
        var unregistered = Record.Exception(() => catalog.Describe(typeof(Other)));
        _ = catalog.Register<Other>(2, "available");
        var described = catalog.Describe(typeof(Other));
        var availableResolved = catalog.TryResolve("available", 2, out var actual);

        // Assert
        _ = Assert.IsType<InvalidOperationException>(rejection);
        Assert.True(originalResolved);
        Assert.Equal(typeof(Created), original);
        _ = Assert.IsType<InvalidOperationException>(unregistered);
        Assert.Equal(("available", 2), described);
        Assert.True(availableResolved);
        Assert.Equal(typeof(Other), actual);
    }

    sealed record Created : DomainEvent;
    sealed record Other : DomainEvent;
}
