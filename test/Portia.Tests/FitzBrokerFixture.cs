using Cntryl.Fitz;

namespace Cntryl.Portia;

/// <summary>
///     Shares the endpoint of the Docker Compose-managed Fitz broker across the integration tests.
/// </summary>
public sealed class FitzBrokerFixture
{
    const string DefaultEndpoint = "ws://127.0.0.1:4090/ws";

    readonly Uri _endpoint;

    /// <summary>
    ///     Reads the broker endpoint started by Docker Compose.
    /// </summary>
    public FitzBrokerFixture()
    {
        var configuredEndpoint = Environment.GetEnvironmentVariable("FITZ_TEST_ENDPOINT")
                                 ?? DefaultEndpoint;
        _endpoint = new Uri(configuredEndpoint, UriKind.Absolute);
    }

    /// <summary>
    ///     Creates and connects a real Fitz .NET client to the isolated broker.
    /// </summary>
    /// <returns>A connected client owned by the caller.</returns>
    public async Task<Client> CreateClientAsync()
    {
        var client = new Client(new ClientConfig(
            _endpoint,
            Timeout: TimeSpan.FromSeconds(10)));

        try
        {
            await client.ConnectWhenReadyAsync(
                    new ConnectWhenReadyOptions(TimeSpan.FromSeconds(15)))
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
