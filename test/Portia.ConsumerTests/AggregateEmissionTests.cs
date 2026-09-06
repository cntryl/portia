namespace Cntryl.Portia.Consumer;

public sealed class AggregateEmissionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectsOppositeKindBeforeMetadataOrStateChanges(bool auditFirst)
    {
        var factory = new CountingMetadataFactory();
        var account = new Account(Uuid.CreateVersion4(), factory);
        var first = new Deposited(10);
        var rejected = new Deposited(20);
        if (auditFirst)
            account.Audit(first);
        else
            account.Raise(first);

        _ = Assert.Throws<InvalidOperationException>(() =>
        {
            if (auditFirst)
                account.Raise(rejected);
            else
                account.Audit(rejected);
        });

        Assert.Equal(1, factory.Calls);
        Assert.Equal(auditFirst ? 0 : 10, account.Balance);
        Assert.Equal(auditFirst ? 0UL : 1UL, account.Version);
        _ = Assert.Throws<InvalidOperationException>(() => rejected.Metadata);

        // Same-kind emission is still permitted after rejection.
        if (auditFirst)
            account.Audit(new Declined("second audit"));
        else
            account.Deposit(5);
        Assert.Equal(2, factory.Calls);
        Assert.Equal(auditFirst ? 0 : 15, account.Balance);
        Assert.Equal(auditFirst ? 0UL : 2UL, account.Version);
    }

    sealed class CountingMetadataFactory : IDomainEventMetadataFactory
    {
        public int Calls { get; private set; }

        public DomainEventMetadata Create(Uuid aggregateId, ulong aggregateVersion)
        {
            Calls++;
            return new DomainEventMetadata(Uuid.CreateVersion4(), aggregateId, aggregateVersion, DateTimeOffset.UtcNow);
        }
    }
}
