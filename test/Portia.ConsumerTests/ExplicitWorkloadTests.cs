using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia.Consumer;

public sealed class ExplicitWorkloadTests
{
    [Fact]
    public async Task UnexpectedCoordinatorCompletionFailsVisibly()
    {
        var services = ConsumerHost.CreateServices();
        _ = services.AddSingleton<IWorkloadCoordinator, CompletedCoordinator>();
        _ = services.AddPortia(p => p.AddReactor<FirstReactor>(o => o.Global())).AddWorker();
        await using var provider = services.BuildServiceProvider();
        var worker = Assert.IsAssignableFrom<Microsoft.Extensions.Hosting.BackgroundService>(
            Assert.Single(provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>()));
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
            Func<WorkloadIdentity, ulong, CancellationToken, Task> run, CancellationToken ct = default) => Task.CompletedTask;
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
                _ = services.AddPortiaFitz(configuration);
            var portia = services.AddPortia(_ => { }).AddWorker();
            _ = portia.AddProjector<FirstProjector>(o => o.PerTenant());
            _ = portia.AddProjector<SecondProjector>(o => o.Global());
            if (!infrastructureFirst)
                _ = services.AddPortiaFitz(configuration);
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
        var portia = services.AddPortia(p =>
        {
            _ = p.AddProjector<FirstProjector>(options => options.PerTenant());
            _ = p.AddProjector<SecondProjector>(options => options.Global());
            _ = p.AddReactor<FirstReactor>(options => options.PerTenant());
        });
        var workloads = services.Where(service => service.ServiceType == typeof(WorkloadRegistration))
            .Select(service => Assert.IsType<WorkloadRegistration>(service.ImplementationInstance)).ToArray();
        Assert.Equal(3, workloads.Length);
        Assert.Equal(WorkloadScope.PerTenant, workloads.Single(item => item.ComponentType == typeof(FirstProjector)).Scope);
        Assert.Equal(WorkloadScope.Global, workloads.Single(item => item.ComponentType == typeof(SecondProjector)).Scope);
        Assert.DoesNotContain(services, service => service.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService));
        _ = portia.AddProjector<FirstProjector>(options => options.PerTenant());
        Assert.Equal(3, services.Count(service => service.ServiceType == typeof(WorkloadRegistration)));
    }

    [Fact]
    public void MissingOrConflictingScopeFailsWithoutPartialRegistration()
    {
        var services = new ServiceCollection();
        var portia = services.AddPortia(_ => { });
        var count = services.Count;
        _ = Assert.Throws<InvalidOperationException>(() => portia.AddProjector<FirstProjector>(_ => { }));
        Assert.Equal(count, services.Count);
        _ = Assert.Throws<InvalidOperationException>(() => portia.AddProjector<FirstProjector>(o =>
        {
            o.PerTenant();
            o.Global();
        }));
        Assert.Equal(count, services.Count);
        _ = portia.AddProjector<FirstProjector>(o => o.PerTenant());
        count = services.Count;
        _ = Assert.Throws<InvalidOperationException>(() => portia.AddProjector<FirstProjector>(o => o.Global()));
        Assert.Equal(count, services.Count);
    }
}
