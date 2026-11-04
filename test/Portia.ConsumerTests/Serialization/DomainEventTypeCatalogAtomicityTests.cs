namespace Cntryl.Portia.Consumer;

/// <summary>A rejected registration leaves both catalog constraints unchanged.</summary>
public sealed class DomainEventTypeCatalogAtomicityTests
{
    /// <summary>Rejecting a second discriminator for one type does not reserve the rejected discriminator.</summary>
    [Fact]
    public void ShouldPreserveCatalogGivenRejectedDuplicateClrType()
    {
        var catalog = new DomainEventTypeCatalog().Register<Created>(1, "original");

        Assert.Throws<InvalidOperationException>(() => catalog.Register<Created>(2, "rejected"));

        Assert.Equal(("original", 1), catalog.Describe(typeof(Created)));
        Assert.False(catalog.TryResolve("rejected", 2, out _));
        catalog.Register<Other>(2, "rejected");
        Assert.True(catalog.TryResolve("rejected", 2, out var actual));
        Assert.Equal(typeof(Other), actual);
    }

    /// <summary>Rejecting an occupied discriminator leaves the new CLR type available under another discriminator.</summary>
    [Fact]
    public void ShouldPreserveCatalogGivenRejectedDuplicateDiscriminator()
    {
        var catalog = new DomainEventTypeCatalog().Register<Created>(1, "original");

        _ = Assert.Throws<InvalidOperationException>(() => catalog.Register<Other>(1, "original"));

        Assert.True(catalog.TryResolve("original", 1, out var original));
        Assert.Equal(typeof(Created), original);
        _ = Assert.Throws<InvalidOperationException>(() => catalog.Describe(typeof(Other)));
        _ = catalog.Register<Other>(2, "available");
        Assert.Equal(("available", 2), catalog.Describe(typeof(Other)));
        Assert.True(catalog.TryResolve("available", 2, out var actual));
        Assert.Equal(typeof(Other), actual);
    }

    sealed record Created : DomainEvent;
    sealed record Other : DomainEvent;
}
