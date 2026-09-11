using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cntryl.Portia.Consumer;

public sealed partial class ComponentHostingTests
{
    /// <summary>
    ///     A notification is only a wake-up: the worker still re-reads durable state every poll
    ///     interval in case a signal was lost. That backstop must be measured by the application's
    ///     own clock, the same one every other delay in the loop already uses — otherwise a host that
    ///     controls time still waits out the interval in real seconds.
    /// </summary>
    [Fact]
    public async Task NotificationBackstopIsScheduledOnTheInjectedClock()
    {
        var pollInterval = TimeSpan.FromMinutes(10);
        var clock = new ManualClock();
        var changes = new Changes();
        var services = ConsumerHost.CreateServices();
        _ = services.AddSingleton<TimeProvider>(clock);
        _ = services.AddSingleton<IDomainEventNotifier>(changes);
        _ = services.AddPortia().AddProjector<FirstProjector>(WorkloadScope.Global,
            options => options.PollInterval = pollInterval).AddWorkers();
        await using var provider = ConsumerHost.Build(services);
        var effects = provider.GetRequiredService<ConsumerHost.Effects>();
        var id = Uuid.CreateVersion4();
        await ConsumerHost.SeedAsync(provider, id);
        var worker = Assert.Single(provider.GetServices<IHostedService>().OfType<BackgroundService>());
        try
        {
            await worker.StartAsync(default);
            await effects.WaitForAsync("first-projector");
            Assert.Equal(pollInterval, await clock.WaitForDelayAsync());
            await ConsumerHost.SeedAsync(provider, id, 1);
            clock.Advance(pollInterval);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (effects.Items.Count < 2)
                await Task.Delay(10, timeout.Token);
        }
        finally
        {
            await worker.StopAsync(default);
            (worker as IDisposable)?.Dispose();
        }

        Assert.Equal(2, effects.Items.Count);
    }
}
