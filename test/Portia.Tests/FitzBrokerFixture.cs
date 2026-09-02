using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Cntryl.Portia;

/// <summary>
/// Shares one isolated, digest-pinned Fitz broker across the Docker-backed integration tests.
/// The broker binds to a random loopback port, so the suite can run beside other local Fitz
/// stacks without claiming one of their fixed ports.
/// </summary>
public sealed class FitzBrokerFixture : IAsyncLifetime
{
    const ushort FitzHttpPort = 4090;
    const string DefaultImage =
        "ghcr.io/cntryl/fitz@sha256:987770b7039873313a1c4d0102f06ea8eed0e5435550ea28d5d8107521f0b287";

    readonly IContainer _container;

    /// <summary>
    /// Creates the disposable Fitz test container definition.
    /// </summary>
    public FitzBrokerFixture()
    {
        var image = Environment.GetEnvironmentVariable("PORTIA_FITZ_TEST_IMAGE") ?? DefaultImage;
        _container = new ContainerBuilder(image)
            .WithEnvironment("FITZ_STORAGE_MODE", "local")
            .WithEnvironment("FITZ_STORAGE_PATH", "/tmp/fitz")
            .WithEnvironment("FITZ_AUTH_REQUIRED", "false")
            .WithEnvironment("FITZ_ADMIN_AUTH_MODE", "open")
            .WithEnvironment("FITZ_HTTP_PORT", "4090")
            .WithEnvironment("FITZ_TCP_ENABLED", "false")
            .WithPortBinding(FitzHttpPort, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request
                .ForPort(FitzHttpPort)
                .ForPath("/healthz")))
            .Build();
    }

    /// <inheritdoc />
    public Task InitializeAsync() => _container.StartAsync();

    /// <inheritdoc />
    public async Task DisposeAsync() => await _container.DisposeAsync().ConfigureAwait(false);

    /// <summary>
    /// Creates and connects a real Fitz .NET client to the isolated broker.
    /// </summary>
    /// <returns>A connected client owned by the caller.</returns>
    public async Task<Fitz.Client> CreateClientAsync()
    {
        var endpoint = new UriBuilder(
            "ws",
            _container.Hostname,
            _container.GetMappedPublicPort(FitzHttpPort),
            "ws").Uri;
        var client = new Fitz.Client(new Fitz.ClientConfig(
            endpoint,
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
