namespace Cntryl.Portia;

/// <summary>
///     Verifies exception messages that reach error logs and traces carry no tenant or aggregate
///     identifiers, while the identifying values stay available as properties.
/// </summary>
public sealed class TenantFreeMessageTests
{
    const string Tenant = "acme-secret-tenant";

    /// <summary>A terminal workload failure names the workload, not its tenant.</summary>
    [Fact]
    public void ShouldOmitTenantGivenWorkloadFailure()
    {
        var identity = new WorkloadIdentity("orders", new TenantId(Tenant));

        var ex = new WorkloadFailureException(identity, 3, new InvalidOperationException("pass failed"));

        Assert.DoesNotContain(Tenant, ex.Message, StringComparison.Ordinal);
        Assert.Contains("orders", ex.Message, StringComparison.Ordinal);
        Assert.Contains("3", ex.Message, StringComparison.Ordinal);
        Assert.Equal(identity, ex.Identity);
    }

    /// <summary>A tenant stop timeout reports the budget, not the tenant.</summary>
    [Fact]
    public void ShouldOmitTenantGivenTenantStopTimeout()
    {
        var ex = new TenantStopTimeoutException(new TenantId(Tenant), TimeSpan.FromSeconds(5));

        Assert.DoesNotContain(Tenant, ex.Message, StringComparison.Ordinal);
        Assert.Contains(TimeSpan.FromSeconds(5).ToString(), ex.Message, StringComparison.Ordinal);
        Assert.Equal(new TenantId(Tenant), ex.TenantId);
    }

    /// <summary>Rebinding a reactor names the workloads, not their tenants.</summary>
    [Fact]
    public void ShouldOmitTenantGivenReactorRebound()
    {
        var reactor = new TestReactor(new RecordingAggregateRepository(),
            pattern: EventStreamPattern.ForTenant("reactors"));
        reactor.BindWorkload(new WorkloadIdentity("test-reactor", new TenantId(Tenant)), null);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            reactor.BindWorkload(new WorkloadIdentity("test-reactor", new TenantId("other-secret")), null));

        Assert.DoesNotContain(Tenant, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("other-secret", ex.Message, StringComparison.Ordinal);
        Assert.Contains("test-reactor", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Rebinding a projector names the workloads, not their tenants.</summary>
    [Fact]
    public void ShouldOmitTenantGivenProjectorRebound()
    {
        var projector = new TestProjector(new RecordingProjectionTarget(), EventStreamPattern.ForTenant("orders"));
        projector.BindWorkload(new WorkloadIdentity("orders", new TenantId(Tenant)), null);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            projector.BindWorkload(new WorkloadIdentity("orders", new TenantId("other-secret")), null));

        Assert.DoesNotContain(Tenant, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("other-secret", ex.Message, StringComparison.Ordinal);
        Assert.Contains("orders", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A malformed stored stream route is reported without echoing the route.</summary>
    [Fact]
    public void ShouldOmitRouteGivenMalformedStreamRoute()
    {
        var ex = Assert.Throws<FormatException>(() => EventStreamAddress.Parse($"stream://{Tenant}/orders"));

        Assert.DoesNotContain(Tenant, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A foreign event is rejected without naming either aggregate ID.</summary>
    [Fact]
    public void ShouldOmitAggregateIdsGivenForeignEvent()
    {
        var aggregate = new TestAggregate(Uuid.CreateVersion4());
        var foreignId = Uuid.CreateVersion4();
        var ev = new ValueChanged(42);
        ev.AttachMetadata(new DomainEventMetadata(Uuid.CreateVersion4(), foreignId, 1, DateTimeOffset.UtcNow));

        var ex = Assert.Throws<InvalidOperationException>(() => aggregate.Load([ev]));

        Assert.DoesNotContain(aggregate.Id.ToString(), ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(foreignId.ToString(), ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
