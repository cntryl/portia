using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cntryl.Portia.Consumer;

public sealed class ConsumerRecoveryTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task HostedConsumerRestartsAfterFaultOrUnexpectedCompletion(bool notification, bool fault, bool stopDuringBackoff)
    {
        var clock = new ManualClock();
        var consumer = new RecoveringConsumer(fault);
        var services = ConsumerHost.CreateServices();
        _ = services.AddPortiaModule<AccountsModule>();
        _ = services.AddScoped<IRequestActorValidator, DeliveryScopeTests.ScopeValidator>();
        _ = services.AddSingleton<TimeProvider>(clock);
        if (notification)
        {
            _ = services.AddSingleton<IRequestNotificationConsumer>(consumer);
            _ = services.AddPortiaRequestNotificationRunner();
        }
        else
        {
            _ = services.AddSingleton<IRequestQueueConsumer>(consumer);
            _ = services.AddPortiaQueueRunner();
        }
        await using var provider = ConsumerHost.Build(services);
        var worker = Assert.IsAssignableFrom<BackgroundService>(Assert.Single(provider.GetServices<IHostedService>()));
        using var cancellation = new CancellationTokenSource();
        try
        {
            await worker.StartAsync(default);
            var delay = clock.WaitForDelayAsync(cancellation.Token);
            var first = await Task.WhenAny(delay, worker.ExecuteTask!).WaitAsync(TimeSpan.FromSeconds(5));
            await first;
            Assert.Same(delay, first);
            Assert.Equal(TimeSpan.FromSeconds(1), await delay);
            Assert.Equal(1, consumer.Starts);
            Assert.Equal(1, consumer.Disposals);
            if (!stopDuringBackoff)
            {
                clock.Advance(TimeSpan.FromSeconds(1));
                await provider.GetRequiredService<ConsumerHost.Effects>().WaitForAsync("nested");
                Assert.Equal(2, consumer.Starts);
            }
        }
        finally
        {
            cancellation.Cancel();
            await worker.StopAsync(default);
            worker.Dispose();
        }
        Assert.Equal(stopDuringBackoff ? 1 : 2, consumer.Disposals);
        Assert.All(provider.GetRequiredService<ConsumerHost.Effects>().Scopes.Values, Assert.True);
        clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(stopDuringBackoff ? 1 : 2, consumer.Starts);
    }

    sealed class RecoveringConsumer(bool fault) : IRequestQueueConsumer, IRequestNotificationConsumer
    {
        public int Starts { get; private set; }
        public int Disposals { get; private set; }

        async IAsyncEnumerable<IQueuedRequest> IRequestQueueConsumer.ReadAsync([EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var request in ReadAsync(ct))
                yield return new Queued(request);
        }

        async IAsyncEnumerable<RequestNotification> IRequestNotificationConsumer.ReadAsync([EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var request in ReadAsync(ct))
                yield return new RequestNotification(request, null);
        }

        async IAsyncEnumerable<ScopeRequest> ReadAsync([EnumeratorCancellation] CancellationToken ct)
        {
            try
            {
                if (++Starts == 1)
                {
                    if (fault)
                        throw new IOException("Transport disconnected");
                    yield break;
                }
                yield return new ScopeRequest(Uuid.CreateVersion7());
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            finally { Disposals++; }
        }
    }

    sealed class Queued(IRequest request) : IQueuedRequest
    {
        public IRequest Request => request;
        public string? ActorToken => null;
        public uint Attempt => 1;
        public ValueTask CompleteAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask AbandonAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
    }
}
