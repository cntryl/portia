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
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }
}
