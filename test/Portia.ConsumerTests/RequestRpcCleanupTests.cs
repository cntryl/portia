using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia.Consumer;

public sealed class RequestRpcCleanupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldCleanupEveryWorkerGivenRegistrationOrDisposalFailureWhenRegistering(bool registrationFails)
    {
        var services = new ServiceCollection();
        _ = services.AddSingleton<RequestHandlerRegistration>(
            new RequestRegistration<FeatureOneRequest, FeatureOneHandler, int>());
        var handles = new[] { new Handle(), new Handle { Fail = true }, new Handle() };
        foreach (var handle in handles)
        {
            _ = services.AddSingleton(new RequestTransportRegistration(typeof(FeatureOneRequest),
                RequestTransports.Callable,
                new RequestRouteAttribute("consumer", "rpc", "cleanup", "get"),
                new DiscriminatorAttribute("consumer.modules.feature-one"),
                (_, _) => ValueTask.FromResult<IAsyncDisposable>(handle)));
        }

        if (registrationFails)
        {
            _ = services.AddSingleton<RequestHandlerRegistration>(
                new RequestRegistration<FeatureTwoRequest, FeatureTwoHandler, int>());
            _ = services.AddSingleton(new RequestTransportRegistration(typeof(FeatureTwoRequest),
                RequestTransports.Callable,
                new RequestRouteAttribute("consumer", "rpc", "cleanup", "fail"),
                new DiscriminatorAttribute("consumer.modules.feature-two"),
                (_, _) => ValueTask.FromException<IAsyncDisposable>(
                    new InvalidOperationException("Registration failed"))));
        }

        await using var provider = services.BuildServiceProvider();
        var server =
            new FitzRpcRequestServer(new InMemoryRpcClient(), provider.GetRequiredService<IServiceScopeFactory>());
        if (registrationFails)
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await server.RegisterRequestsAsync());
            Assert.Equal("Registration failed", error.Message);
        }
        else
        {
            var workers = await server.RegisterRequestsAsync();
            _ = await Assert.ThrowsAsync<AggregateException>(async () => await workers.DisposeAsync());
            await workers.DisposeAsync();
        }

        Assert.All(handles, handle => Assert.Equal(1, handle.Disposals));
    }

    sealed class Handle : IAsyncDisposable
    {
        public bool Fail { get; init; }
        public int Disposals { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposals++;
            return Fail ? ValueTask.FromException(new IOException("Unregister failed")) : ValueTask.CompletedTask;
        }
    }
}
