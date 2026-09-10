using Cntryl.Fitz;

namespace Cntryl.Portia.Consumer;

static class ConsumerBroker
{
    public static async Task<Client> ConnectAsync()
    {
        var endpoint = new Uri(Environment.GetEnvironmentVariable("FITZ_TEST_ENDPOINT") ?? "ws://127.0.0.1:4090/ws");
        var client = new Client(new ClientConfig(endpoint, Timeout: TimeSpan.FromSeconds(10)));
        try
        {
            await client.ConnectWhenReadyAsync(new ConnectWhenReadyOptions(TimeSpan.FromSeconds(15)));
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }
}
