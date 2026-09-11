using Cntryl.Fitz;
using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia.Consumer;

sealed class StoreFixture : IAsyncDisposable
{
    readonly Client? _client;
    readonly ServiceProvider _provider;
    readonly AsyncServiceScope _scope;

    StoreFixture(IEventStore store, Client? client)
    {
        Store = store;
        _client = client;
        var services = new ServiceCollection();
        _ = services.AddSingleton(store);
        _ = services.AddPortia();
        _provider = services.BuildServiceProvider(new ServiceProviderOptions
        { ValidateScopes = true, ValidateOnBuild = true });
        _scope = _provider.CreateAsyncScope();
        Repository = _scope.ServiceProvider.GetRequiredService<IAggregateRepository>();
    }

    public IEventStore Store { get; }

    public IAggregateRepository Repository { get; }

    public async ValueTask DisposeAsync()
    {
        await _scope.DisposeAsync();
        await _provider.DisposeAsync();
        if (_client is not null)
        {
            await _client.DisposeAsync();
        }
    }

    public static async Task<StoreFixture> CreateAsync(bool fitz)
    {
        if (!fitz)
        {
            return new StoreFixture(new InMemoryEventStore(), null);
        }

        var endpoint = new Uri(Environment.GetEnvironmentVariable("FITZ_TEST_ENDPOINT") ?? "ws://127.0.0.1:4090/ws");
        var client = new Client(new ClientConfig(endpoint, Timeout: TimeSpan.FromSeconds(10)));
        try
        {
            await client.ConnectWhenReadyAsync(new ConnectWhenReadyOptions(TimeSpan.FromSeconds(15)));
            return new StoreFixture(new FitzEventStore(client.Stream,
                ConsumerJson.DomainSerializer(new DomainEventTypeCatalog().Register<Deposited>(1, "Deposited")
                    .Register<Declined>(1, "Declined"))), client);
        }
        catch (Exception ex)
        {
            await client.DisposeAsync();
            throw Unreachable(endpoint, ex);
        }
    }

    // A suite that needs the Compose-managed broker should say so. Without this the failure is a
    // WebSocket EOF or an authentication error from deep inside the client, which says nothing
    // about the broker being absent, misconfigured, or listening on a different port.
    static InvalidOperationException Unreachable(Uri endpoint, Exception cause) => new(
        $"No Fitz broker answered at '{endpoint}'. Start it with 'docker compose up -d' from the repository "
        + "root, or point FITZ_TEST_ENDPOINT at a running broker. The Compose broker runs with "
        + "authentication disabled; a broker that requires credentials fails here the same way.", cause);
}
