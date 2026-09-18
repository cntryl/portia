namespace Cntryl.Portia;

/// <summary>
///     Verifies <see cref="InMemoryLeaseClient" /> is an honest <see cref="IPartitionLeaseCompetitor" />
///     substitute: since <see cref="FleetPartitionRunner" /> only ever depends on the narrow
///     <see cref="IPartitionLeaseCompetitor" /> interface (not the full Fitz <c>ILeaseClient</c>),
///     this fake has no unimplemented, throwing members left over — every member of the interface it
///     claims to implement actually works. <see cref="FleetPartitionRunnerTests" /> already proves it
///     can be passed anywhere a <see cref="FleetPartitionRunner" /> expects one; this verifies the
///     interface's one member directly.
/// </summary>
public sealed class InMemoryLeaseClientTests
{
    /// <summary>
    ///     Verifies that acquiring a lease runs the callback and records the acquisition, with no
    ///     <see cref="NotSupportedException" /> anywhere in the interface's one member.
    /// </summary>
    [Fact]
    public async Task ShouldRunCallbackAndRecordAcquisition()
    {
        var leases = new InMemoryLeaseClient();
        var ran = false;

        await leases.WithLeaseAsync("lease://portia/fleet/p", 30, _ =>
        {
            ran = true;
            return ValueTask.CompletedTask;
        });

        Assert.True(ran);
        Assert.Equal(["lease://portia/fleet/p"], leases.Acquisitions);
    }
}
