using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia.Testing;

/// <summary>
///     Composes a minimal Portia-backed service provider for tests: an <see cref="IEventStore" />,
///     <c>AddPortia()</c>, and a scope exposing the resulting aggregate <see cref="Repository" />.
/// </summary>
public sealed class StoreFixture : IAsyncDisposable
{
    readonly ServiceProvider _provider;
    readonly AsyncServiceScope _scope;

    /// <summary>Creates the fixture.</summary>
    /// <param name="store">The event store to use, or <see langword="null" /> for a new <see cref="InMemoryEventStore" />.</param>
    public StoreFixture(IEventStore? store = null)
    {
        Store = store ?? new InMemoryEventStore();
        var services = new ServiceCollection();
        _ = services.AddSingleton(Store);
        _ = services.AddPortia();
        _provider = services.BuildServiceProvider(new ServiceProviderOptions
        { ValidateScopes = true, ValidateOnBuild = true });
        _scope = _provider.CreateAsyncScope();
        Repository = _scope.ServiceProvider.Aggregates();
    }

    /// <summary>Gets the fixture's event store.</summary>
    public IEventStore Store { get; }

    /// <summary>Gets the fixture's scoped service provider, for resolving anything <c>AddPortia()</c> registers.</summary>
    public IServiceProvider Services => _scope.ServiceProvider;

    /// <summary>Gets the scope's combined aggregate read/write capabilities.</summary>
    public AggregateCapabilities Repository { get; }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _scope.DisposeAsync();
        await _provider.DisposeAsync();
    }
}
