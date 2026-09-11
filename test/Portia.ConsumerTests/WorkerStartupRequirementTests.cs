using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cntryl.Portia.Consumer;

/// <summary>
///     Verifies that a worker host refuses to start when the infrastructure its declared components read
///     through is missing. Without this, a projector or reactor starts, polls a store that is not there,
///     and the deployment looks healthy while making no progress at all — a silent failure that is only
///     noticed when someone asks why a projection is stale.
/// </summary>
public sealed class WorkerStartupRequirementTests
{
    [Fact]
    public async Task WorkersWithoutAnEventReaderFailBeforeServing()
    {
        var builder = Host.CreateApplicationBuilder();
        _ = builder.Services.AddPortia().AddProjector<FirstProjector>(WorkloadScope.Global).AddWorkers();
        using var host = builder.Build();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());

        Assert.Contains(nameof(IDomainEventReader), error.Message, StringComparison.Ordinal);
        Assert.Contains("Register its application or infrastructure implementation", error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task PerTenantWorkersAlsoRequireATenantDirectory()
    {
        var builder = Host.CreateApplicationBuilder();
        foreach (var descriptor in ConsumerHost.CreateServices())
            builder.Services.Add(descriptor);
        _ = builder.Services.AddPortia().AddProjector<FirstProjector>(WorkloadScope.PerTenant).AddWorkers();
        using var host = builder.Build();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());

        Assert.Contains(nameof(ITenantDirectory), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GlobalWorkersDoNotRequireATenantDirectory()
    {
        var builder = Host.CreateApplicationBuilder();
        foreach (var descriptor in ConsumerHost.CreateServices())
            builder.Services.Add(descriptor);
        _ = builder.Services.AddPortia()
            .AddProjector<FirstProjector>(WorkloadScope.Global, options => options.PollInterval = TimeSpan.FromDays(1))
            .AddWorkers();
        using var host = builder.Build();

        await host.StartAsync();
        await host.StopAsync();
    }

    [Fact]
    public async Task AHostThatDeclaredNoComponentsRequiresNothing()
    {
        var builder = Host.CreateApplicationBuilder();
        _ = builder.Services.AddPortia().AddWorkers();
        using var host = builder.Build();

        await host.StartAsync();
        await host.StopAsync();
    }
}
