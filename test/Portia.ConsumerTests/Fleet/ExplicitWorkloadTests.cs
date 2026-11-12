using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cntryl.Portia.Consumer;

public sealed class ExplicitWorkloadTests
{
    [Fact]
    public void ShouldActivateDeclaredWorkloadsGivenApplicationWhenAddingWorkers()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        _ = services.AddPortia()
            .AddReactor<FirstReactor>("first-reactor", WorkloadScope.Global)
            .AddWorkers();

        // Assert
        Assert.Equal(2, services.Count(service => service.ServiceType == typeof(IHostedService)));
        _ = Assert.Single(services, service => service.ServiceType == typeof(IHostedService)
                                               && service.ImplementationType?.Name == "PortiaWorkloadService");
    }

    [Fact]
    public async Task UnexpectedCoordinatorCompletionFailsVisibly()
    {
        // Arrange
        var services = ConsumerHost.CreateServices();
        _ = services.AddSingleton<IWorkloadCoordinator, CompletedCoordinator>();
        _ = services.AddPortia().AddReactor<FirstReactor>("first-reactor", WorkloadScope.Global).AddWorkers();

        // Act
        await using var provider = services.BuildServiceProvider();

        // Assert
        var worker = Assert.Single(provider.GetServices<IHostedService>().OfType<BackgroundService>());
        await worker.StartAsync(default);
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Contains("coordinator", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await worker.StopAsync(default);
        }
    }

    [Fact]
    public async Task ShouldFailStartGivenNoCoordinatorWhenStartingWorkers()
    {
        // Arrange
        var services = ConsumerHost.CreateServices();
        _ = services.AddPortia().AddReactor<FirstReactor>("first-reactor", WorkloadScope.Global).AddWorkers();

        // Act
        await using var provider = services.BuildServiceProvider();

        // Assert
        var worker = Assert.Single(provider.GetServices<IHostedService>().OfType<BackgroundService>());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => worker.StartAsync(default));

        Assert.Contains(nameof(IWorkloadCoordinator), error.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(PortiaBuilder.UseSingleProcessWorkloads), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShouldFailStartGivenTwoCoordinatorsWhenStartingWorkers()
    {
        // Arrange
        var services = ConsumerHost.CreateServices();
        _ = services.AddSingleton<IWorkloadCoordinator, CompletedCoordinator>();
        _ = services.AddPortia().AddReactor<FirstReactor>("first-reactor", WorkloadScope.Global)
            .UseSingleProcessWorkloads().AddWorkers();

        // Act
        await using var provider = services.BuildServiceProvider();

        // Assert
        var worker = Assert.Single(provider.GetServices<IHostedService>().OfType<BackgroundService>());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => worker.StartAsync(default));

        Assert.Contains("exactly one", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ShouldRegisterSingleProcessCoordinatorGivenOptInWhenUsingSingleProcessWorkloads()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        _ = services.AddPortia().UseSingleProcessWorkloads();

        // Assert
        var descriptor = Assert.Single(services, item => item.ServiceType == typeof(IWorkloadCoordinator));
        using var provider = services.BuildServiceProvider();
        _ = Assert.IsType<SingleProcessWorkloadCoordinator>(provider.GetRequiredService<IWorkloadCoordinator>());
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    [Fact]
    public void InfrastructureRegistrationOrderDoesNotChangeWorkloadDeclarations()
    {
        // Arrange
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            { ["Endpoint"] = "ws://127.0.0.1:1/ws", ["ApplicationName"] = "registration-test" }).Build();

        // Act
        var registrations = new List<(int Workloads, int Coordinators)>();
        foreach (var infrastructureFirst in new[] { true, false })
        {
            var services = new ServiceCollection();
            if (infrastructureFirst)
            {
                _ = services.AddPortia().AddFitz(configuration);
            }

            var portia = services.AddPortia().AddWorkers();
            _ = portia.AddProjector<TenantFirstProjector>("first-projector", WorkloadScope.PerTenant);
            _ = portia.AddProjector<SecondProjector>("second-projector", WorkloadScope.Global);
            if (!infrastructureFirst)
            {
                _ = services.AddPortia().AddFitz(configuration);
            }

            registrations.Add((services.Count(item => item.ServiceType == typeof(WorkloadRegistration)),
                services.Count(item => item.ServiceType == typeof(IWorkloadCoordinator))));
        }

        // Assert
        Assert.All(registrations, registration =>
        {
            Assert.Equal(2, registration.Workloads);
            Assert.Equal(1, registration.Coordinators);
        });
        Assert.DoesNotContain(typeof(PortiaFitzBuilder).GetMethods(),
            method => method.Name is "AddProjector" or "AddReactor");
        Assert.DoesNotContain(typeof(PortiaBuilder).Assembly.GetExportedTypes(),
            type => type.Name.Contains("Module", StringComparison.Ordinal));
    }

    [Fact]
    public void MixedScopesRegisterWithoutFitzOrStartingWorkers()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        var portia = services.AddPortia()
            .AddProjector<TenantFirstProjector>("first-projector", WorkloadScope.PerTenant)
            .AddProjector<SecondProjector>("second-projector", WorkloadScope.Global)
            .AddReactor<TenantFirstReactor>("first-reactor", WorkloadScope.PerTenant);

        // Assert
        var workloads = services.Where(service => service.ServiceType == typeof(WorkloadRegistration))
            .Select(service => Assert.IsType<WorkloadRegistration>(service.ImplementationInstance)).ToArray();
        Assert.Equal(3, workloads.Length);
        Assert.Equal(WorkloadScope.PerTenant,
            workloads.Single(item => item.ComponentType == typeof(TenantFirstProjector)).Scope);
        Assert.Equal(WorkloadScope.Global,
            workloads.Single(item => item.ComponentType == typeof(SecondProjector)).Scope);
        Assert.DoesNotContain(services, service => service.ServiceType == typeof(IHostedService)
                                                   && service.ImplementationType is not null
                                                   && typeof(BackgroundService).IsAssignableFrom(
                                                       service.ImplementationType));
        _ = portia.AddProjector<TenantFirstProjector>("first-projector", WorkloadScope.PerTenant);
        Assert.Equal(3, services.Count(service => service.ServiceType == typeof(WorkloadRegistration)));
    }

    [Fact]
    public void DuplicateWorkloadRejectsDifferentFailurePolicy()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        var portia = services.AddPortia().AddProjector<FirstProjector>("first-projector", WorkloadScope.Global,
            options => options.FailureAttemptLimit = 3);

        // Assert
        var exception = Assert.Throws<InvalidOperationException>(() =>
            portia.AddProjector<FirstProjector>("first-projector", WorkloadScope.Global,
                options => options.FailureAttemptLimit = 4));

        Assert.Contains("Conflicting workload registration", exception.Message, StringComparison.Ordinal);
        _ = Assert.Single(services, service => service.ServiceType == typeof(WorkloadRegistration));
    }

    [Fact]
    public void ShouldPreserveServicesGivenInvalidOrConflictingScopeWhenRegisteringWorkload()
    {
        // Arrange
        var services = new ServiceCollection();
        var portia = services.AddPortia();

        // Act
        var count = services.Count;

        // Assert
        _ = Assert.Throws<ArgumentOutOfRangeException>(() =>
            portia.AddProjector<FirstProjector>("first-projector", (WorkloadScope)42));
        Assert.Equal(count, services.Count);
        _ = portia.AddProjector<TenantFirstProjector>("first-projector", WorkloadScope.PerTenant);
        count = services.Count;
        _ = Assert.Throws<InvalidOperationException>(() =>
            portia.AddProjector<TenantFirstProjector>("first-projector", WorkloadScope.Global));
        Assert.Equal(count, services.Count);
    }

    sealed class CompletedCoordinator : IWorkloadCoordinator
    {
        public Task RunAsync(Func<IReadOnlyCollection<WorkloadIdentity>> workloads,
            Func<WorkloadIdentity, CancellationToken, Task> run, CancellationToken ct = default) => Task.CompletedTask;
    }
}
