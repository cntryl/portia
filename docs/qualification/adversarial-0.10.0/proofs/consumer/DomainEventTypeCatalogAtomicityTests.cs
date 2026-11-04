namespace Cntryl.Portia.Consumer;

public sealed class DomainEventTypeCatalogAtomicityTests
{
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

    sealed record Created : DomainEvent;
    sealed record Other : DomainEvent;
}
