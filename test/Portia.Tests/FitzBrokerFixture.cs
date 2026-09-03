namespace Cntryl.Portia;

/// <summary>
/// Shares the endpoint of the Docker Compose-managed Fitz broker across the integration tests.
/// </summary>
public sealed class FitzBrokerFixture
{
    const string DefaultEndpoint = "ws://127.0.0.1:4090/ws";

    readonly Uri _endpoint;

    /// <summary>
    /// Reads the broker endpoint started by Docker Compose.
    /// </summary>
    public FitzBrokerFixture()
    {
        var configuredEndpoint = Environment.GetEnvironmentVariable("PORTIA_FITZ_TEST_ENDPOINT")
            ?? DefaultEndpoint;
        _endpoint = new Uri(configuredEndpoint, UriKind.Absolute);
    }

    /// <summary>
    /// Creates and connects a real Fitz .NET client to the isolated broker.
    /// </summary>
    /// <returns>A connected client owned by the caller.</returns>
    public async Task<Fitz.Client> CreateClientAsync()
    {
        var client = new Fitz.Client(new Fitz.ClientConfig(
            _endpoint,
            Timeout: TimeSpan.FromSeconds(10)));

        try
        {
            await client.ConnectWhenReadyAsync(
                new Fitz.ConnectWhenReadyOptions(Timeout: TimeSpan.FromSeconds(15)))
                .ConfigureAwait(false);
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

/// <summary>
/// Prevents the shared broker fixture from running concurrently with another collection instance.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class FitzBrokerCollectionDefinition : ICollectionFixture<FitzBrokerFixture>
{
    /// <summary>
    /// Identifies the live Fitz integration-test collection.
    /// </summary>
    public const string Name = "Fitz broker integration";
}
