using System.Reflection;

namespace Cntryl.Portia;

/// <summary>Verifies the argument contract of direct Fitz fleet membership.</summary>
public sealed class FitzFleetMembershipTests
{
    /// <summary>
    ///     Membership without a worker ID is refused: registering under an ID generated here would leave
    ///     the caller unable to find itself in the inventory it is handed.
    /// </summary>
    [Fact]
    public async Task ShouldRejectMembershipGivenMissingWorkerId()
    {
        // Arrange
        var membership = new FitzFleetMembership(DispatchProxy.Create<ILeaseClient, UnusedLeaseClient>());
        var options = new FleetRunOptions { MembershipSelector = "lease://portia/members/*" };

        // Act
        var exception = await Record.ExceptionAsync(() => membership.RunAsync(options, (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken));

        // Assert
        var error = Assert.IsType<ArgumentException>(exception);
        Assert.Contains(nameof(FleetRunOptions.WorkerId), error.Message, StringComparison.Ordinal);
    }
}
