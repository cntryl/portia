using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

/// <summary>
///     Covers how <see cref="FitzRpcRequestServer" /> resolves the generated transport catalog when it
///     was not handed one at construction.
/// </summary>
public sealed class FitzRpcCatalogResolutionTests
{
    /// <summary>
    ///     Verifies the fallback resolution happens once for the life of the server even when two
    ///     registrations race. The catalog is immutable, so a duplicate build is not wrong — but it
    ///     opens a dependency-injection scope and re-reads every generated descriptor to produce a
    ///     value that is then thrown away.
    /// </summary>
    [Fact]
    public async Task ShouldResolveTheTransportCatalogOnceUnderConcurrentRegistration()
    {
        using var busHost = TestRequestBus.Create();
        using var scopes = new CountingScopeFactory(busHost.ScopeFactory);
        var server = new FitzRpcRequestServer(new InMemoryRpcClient(), scopes);

        var registrations = await Task.WhenAll(
            Task.Run(async () => await server.RegisterAsync<RpcChangeValue>()),
            Task.Run(async () => await server.RegisterAsync<RpcChangeValue>()));

        foreach (var registration in registrations)
            await registration.DisposeAsync();

        Assert.Equal(1, scopes.Scopes);
    }

    sealed class CountingScopeFactory(IServiceScopeFactory inner) : IServiceScopeFactory, IDisposable
    {
        readonly SemaphoreSlim _arrived = new(0);
        int _scopes;

        public int Scopes => Volatile.Read(ref _scopes);

        public IServiceScope CreateScope()
        {
            // Make the race deterministic instead of hoping for an interleaving: the first caller
            // waits briefly for a second, which only ever arrives when resolution is unguarded.
            _ = Interlocked.Increment(ref _scopes);
            _ = _arrived.Release();
            _ = _arrived.Wait(TimeSpan.FromMilliseconds(250));
            return inner.CreateScope();
        }

        public void Dispose() => _arrived.Dispose();
    }
}
