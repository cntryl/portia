using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia.Consumer;

public sealed class OperationalTopologyTests
{
    [Fact]
    public void ShouldDescribeOnlyRegisteredCrossAssemblyProcessorsWithoutResolvingDependencies()
    {
        var services = new ServiceCollection();
        services.AddContracts();
        services.AddPortia()
            .AddReactor<FirstReactor>("first", WorkloadScope.Global)
            .AddReactor<TenantFirstReactor>("tenant", WorkloadScope.PerTenant)
            .AddFitz(new Cntryl.Fitz.ClientConfig(new Uri("ws://127.0.0.1:1/ws")), fitz =>
                fitz.UseOperations("kv://test/state/progress", options => options.QuiescedWorkerCutover = true));
        // These processors have unresolved application dependencies and Fitz is unreachable.
        // Describing generated registration metadata must require neither of them.
        using var provider = services.BuildServiceProvider();
        var topology = provider.GetRequiredService<IApplicationTopology>().Describe();
        Assert.Equal(["first", "tenant"], topology.Processors.Select(p => p.Name));
        var remote = Assert.Single(topology.Requests, request => request.Request == typeof(FeatureOneRequest).FullName);
        Assert.StartsWith("unknown:", remote.Handler, StringComparison.Ordinal);
        Assert.DoesNotContain(topology.Processors, p => p.Component.Contains("SecondReactor", StringComparison.Ordinal));
        var subscriptions = typeof(FirstReactor).GetCustomAttribute<ProcessorTopologyAttribute>()!.Subscriptions;
        Assert.Equal(2, subscriptions.Count);
        Assert.All(topology.Processors, p => Assert.Equal(subscriptions, p.Subscriptions));
        Assert.All(topology.Processors, p => Assert.Contains("unknown", p.Coverage, StringComparison.Ordinal));
    }
}
