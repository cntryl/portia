namespace Cntryl.Portia;

/// <summary>Operations activation is explicit and cannot silently replace checkpoint ownership.</summary>
public sealed class OperationalConfigurationTests
{
    /// <summary>A new host enables operations without acknowledging historical worker ownership.</summary>
    [Fact]
    public async Task ShouldEnableOperationsGivenNewHost()
    {
        // Arrange
        await using var client = new FitzClient(new ClientConfig(new Uri("ws://127.0.0.1:1/ws")));
        var services = new ServiceCollection();

        // Act
        var exception = Record.Exception(() => services.AddPortia().UseFitzClient(client,
            fitz => fitz.UseOperations("kv://test/state/progress")));

        // Assert
        Assert.Null(exception);
        Assert.Contains(services, service => service.ServiceType == typeof(IOperationalStore));
    }

    /// <summary>Independent and operational checkpoint registrations conflict in either order.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ShouldRejectMixedCheckpointOwnershipGivenOperationsAndCheckpointsInEitherOrder(
        bool operationsFirst)
    {
        // Arrange
        await using var client = new FitzClient(new ClientConfig(new Uri("ws://127.0.0.1:1/ws")));
        var services = new ServiceCollection();
        PortiaFitzBuilder? configured = null;
        services.AddPortia().UseFitzClient(client, fitz =>
        {
            configured = fitz;

            if (operationsFirst)
                fitz.UseOperations("kv://test/state/progress");
            else
                fitz.UseKvCheckpoints("kv://test/state/progress");
        });

        // Act
        var exception = Record.Exception(() => operationsFirst
            ? configured!.UseKvCheckpoints("kv://test/state/progress")
            : configured!.UseOperations("kv://test/state/progress"));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Single(services, service => service.ServiceType == typeof(IProjectionCheckpointStore));
    }

    /// <summary>Service providers borrow domain clients when the application owns the Fitz connection.</summary>
    [Fact]
    public async Task ShouldKeepClientOutsideProviderDisposalGivenBorrowedFitzClient()
    {
        // Arrange
        await using var client = new FitzClient(new ClientConfig(new Uri("ws://127.0.0.1:1/ws")));
        var services = new ServiceCollection();

        // Act
        services.AddPortia().UseFitzClient(client);

        // Assert
        Assert.Same(client.Kv,
            Assert.Single(services, service => service.ServiceType == typeof(IKvClient)).ImplementationInstance);
        Assert.Same(client.Stream,
            Assert.Single(services, service => service.ServiceType == typeof(IStreamClient)).ImplementationInstance);
        Assert.Same(client.Schedule,
            Assert.Single(services, service => service.ServiceType == typeof(IScheduleClient)).ImplementationInstance);
    }
}
