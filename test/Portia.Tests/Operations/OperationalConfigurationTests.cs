namespace Cntryl.Portia;

/// <summary>Operations activation is explicit and cannot silently replace checkpoint ownership.</summary>
public sealed class OperationalConfigurationTests
{
    /// <summary>A host must acknowledge that legacy workers were quiesced.</summary>
    [Fact]
    public async Task ShouldRequireQuiescedCutoverBeforeEnablingOperations()
    {
        await using var client = new Client(new ClientConfig(new Uri("ws://127.0.0.1:1/ws")));
        var services = new ServiceCollection();
        services.AddPortia().UseFitzClient(client, fitz =>
        {
            _ = Assert.Throws<InvalidOperationException>(() => fitz.UseOperations("kv://test/state/progress", _ => { }));
        });
        Assert.DoesNotContain(services, service => service.ServiceType == typeof(IOperationalStore));
    }

    /// <summary>Legacy and operational checkpoint registrations conflict in either order.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ShouldRejectMixedCheckpointOwnership(bool operationsFirst)
    {
        await using var client = new Client(new ClientConfig(new Uri("ws://127.0.0.1:1/ws")));
        var services = new ServiceCollection();
        services.AddPortia().UseFitzClient(client, fitz =>
        {
            if (operationsFirst)
            {
                fitz.UseOperations("kv://test/state/progress", options => options.QuiescedWorkerCutover = true);
                _ = Assert.Throws<InvalidOperationException>(() => fitz.UseKvCheckpoints("kv://test/state/progress"));
            }
            else
            {
                fitz.UseKvCheckpoints("kv://test/state/progress");
                _ = Assert.Throws<InvalidOperationException>(() => fitz.UseOperations("kv://test/state/progress",
                    options => options.QuiescedWorkerCutover = true));
            }
        });
    }

    /// <summary>Service providers borrow domain clients when the application owns the Fitz connection.</summary>
    [Fact]
    public async Task ShouldKeepBorrowedFitzClientsOutsideServiceProviderDisposal()
    {
        await using var client = new Client(new ClientConfig(new Uri("ws://127.0.0.1:1/ws")));
        var services = new ServiceCollection();
        services.AddPortia().UseFitzClient(client);
        Assert.Same(client.Kv, Assert.Single(services, service => service.ServiceType == typeof(IKvClient)).ImplementationInstance);
        Assert.Same(client.Stream, Assert.Single(services, service => service.ServiceType == typeof(IStreamClient)).ImplementationInstance);
        Assert.Same(client.Schedule, Assert.Single(services, service => service.ServiceType == typeof(IScheduleClient)).ImplementationInstance);
    }
}
