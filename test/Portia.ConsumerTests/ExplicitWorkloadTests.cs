using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cntryl.Portia.Consumer;

public sealed class ExplicitWorkloadTests
{
    [Fact]
    public void ShouldActivateDeclaredWorkloadsGivenApplicationWhenAddingWorkers()
    {
        var services = new ServiceCollection();

        _ = services.AddPortia()
            .AddReactor<FirstReactor>(WorkloadScope.Global)
            .AddWorkers();

        Assert.Equal(2, services.Count(service => service.ServiceType == typeof(IHostedService)));
        _ = Assert.Single(services, service => service.ServiceType == typeof(IHostedService)
            && service.ImplementationType?.Name == "PortiaWorkloadService");
    }

    [Fact]
    public async Task UnexpectedCoordinatorCompletionFailsVisibly()
    {
        var services = ConsumerHost.CreateServices();
        _ = services.AddSingleton<IWorkloadCoordinator, CompletedCoordinator>();
        _ = services.AddPortia().AddReactor<FirstReactor>(WorkloadScope.Global).AddWorkers();
        await using var provider = services.BuildServiceProvider();
        var worker = Assert.Single(provider.GetServices<IHostedService>().OfType<BackgroundService>());
        await worker.StartAsync(default);
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Contains("coordinator", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { await worker.StopAsync(default); }
    }

    sealed class CompletedCoordinator : IWorkloadCoordinator
    {
        public Task RunAsync(Func<IReadOnlyCollection<WorkloadIdentity>> workloads,
            Func<WorkloadIdentity, CancellationToken, Task> run, CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public void InfrastructureRegistrationOrderDoesNotChangeWorkloadDeclarations()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Endpoint"] = "ws://127.0.0.1:1/ws", ["ApplicationName"] = "registration-test" }).Build();
        foreach (var infrastructureFirst in new[] { true, false })
        {
            var services = new ServiceCollection();
            if (infrastructureFirst)
                _ = services.AddPortia().AddFitz(configuration);
            var portia = services.AddPortia().AddWorkers();
            _ = portia.AddProjector<FirstProjector>(WorkloadScope.PerTenant);
            _ = portia.AddProjector<SecondProjector>(WorkloadScope.Global);
            if (!infrastructureFirst)
                _ = services.AddPortia().AddFitz(configuration);
            Assert.Equal(2, services.Count(item => item.ServiceType == typeof(WorkloadRegistration)));
            _ = Assert.Single(services, item => item.ServiceType == typeof(IWorkloadCoordinator));
        }
        Assert.DoesNotContain(typeof(PortiaFitzBuilder).GetMethods(), method => method.Name is "AddProjector" or "AddReactor");
        Assert.DoesNotContain(typeof(PortiaBuilder).Assembly.GetExportedTypes(), type => type.Name.Contains("Module", StringComparison.Ordinal));
    }

    [Fact]
    public void MixedScopesRegisterWithoutFitzOrStartingWorkers()
    {
        var services = new ServiceCollection();
        var portia = services.AddPortia()
            .AddProjector<FirstProjector>(WorkloadScope.PerTenant)
            .AddProjector<SecondProjector>(WorkloadScope.Global)
            .AddReactor<FirstReactor>(WorkloadScope.PerTenant);
        var workloads = services.Where(service => service.ServiceType == typeof(WorkloadRegistration))
            .Select(service => Assert.IsType<WorkloadRegistration>(service.ImplementationInstance)).ToArray();
        Assert.Equal(3, workloads.Length);
        Assert.Equal(WorkloadScope.PerTenant, workloads.Single(item => item.ComponentType == typeof(FirstProjector)).Scope);
        Assert.Equal(WorkloadScope.Global, workloads.Single(item => item.ComponentType == typeof(SecondProjector)).Scope);
        Assert.DoesNotContain(services, service => service.ServiceType == typeof(IHostedService));
        _ = portia.AddProjector<FirstProjector>(WorkloadScope.PerTenant);
        Assert.Equal(3, services.Count(service => service.ServiceType == typeof(WorkloadRegistration)));
    }

    [Fact]
    public void ShouldPreserveServicesGivenInvalidOrConflictingScopeWhenRegisteringWorkload()
    {
        var services = new ServiceCollection();
        var portia = services.AddPortia();
        var count = services.Count;
        _ = Assert.Throws<ArgumentOutOfRangeException>(() =>
            portia.AddProjector<FirstProjector>((WorkloadScope)42));
        Assert.Equal(count, services.Count);
        _ = portia.AddProjector<FirstProjector>(WorkloadScope.PerTenant);
        count = services.Count;
        _ = Assert.Throws<InvalidOperationException>(() => portia.AddProjector<FirstProjector>(WorkloadScope.Global));
        Assert.Equal(count, services.Count);
    }
}
