using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cntryl.Portia;

/// <summary>
///     Verifies that a hosted projector whose Fitz KV repository was constructed for a different
///     projector name is rejected when the host starts, before any pass runs, rather than on its first
///     checkpoint load after the runner's retries.
/// </summary>
public sealed class FitzKvProjectionStoreStartupTests
{
    /// <summary>
    ///     A repository built for one projector name, serving a projector registered under another, stops
    ///     the host at startup with both names in the message.
    /// </summary>
    /// <param name="scope">The registered workload scope.</param>
    [Theory]
    [InlineData(WorkloadScope.Global)]
    [InlineData(WorkloadScope.PerTenant)]
    public async Task ShouldRejectStartupGivenRepositoryBuiltForAnotherProjector(WorkloadScope scope)
    {
        // Arrange
        using var host = Host(scope, registeredAs: "orders", repositoryServes: "OrdersProjector");

        // Act
        var thrown = await Record.ExceptionAsync(() => host.StartAsync());

        // Assert
        var error = Assert.IsType<InvalidOperationException>(thrown);
        Assert.Contains("'OrdersProjector'", error.Message, StringComparison.Ordinal);
        Assert.Contains("'orders'", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A repository built for the registered name starts normally.</summary>
    [Fact]
    public async Task ShouldStartGivenRepositoryServingRegisteredProjector()
    {
        // Arrange
        using var host = Host(WorkloadScope.Global, registeredAs: "orders", repositoryServes: "orders");

        // Act
        var exception = await Record.ExceptionAsync(async () =>
        {
            await host.StartAsync();
            await host.StopAsync();
        });

        // Assert
        Assert.Null(exception);
    }

    static IHost Host(WorkloadScope scope, string registeredAs, string repositoryServes)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        _ = builder.Services.AddSingleton<IDomainEventReader, InMemoryEventStore>();
        _ = builder.Services.AddSingleton<ITenantDirectory>(new NoTenants());
        _ = builder.Services.AddScoped(_ => new OrdersRepository(new FakeKvClient(), repositoryServes));
        _ = builder.Services.AddScoped(_ => new OrdersProjector(
            _.GetRequiredService<OrdersRepository>(),
            scope == WorkloadScope.PerTenant
                ? EventStreamPattern.ForTenant("orders")
                : EventStreamPattern.ForPattern("test", "orders")));
        _ = builder.Services.AddPortia().AddProjector<OrdersProjector>(registeredAs, scope).AddWorkers().UseSingleProcessWorkloads();
        return builder.Build();
    }
}

// Per-tenant validation needs a directory registered; no tenant ever becomes active.
sealed class NoTenants : ITenantDirectory
{
    public async IAsyncEnumerable<TenantId> GetActiveTenantsAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    public async IAsyncEnumerable<TenantLifecycleChange> WatchAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Delay(Timeout.Infinite, ct);
        yield break;
    }
}

sealed class OrdersRepository(IKvClient kv, string projector)
    : FitzKvProjectionStore(kv, "kv://portia/state/orders", projector);

sealed partial class OrdersProjector(OrdersRepository orders, EventStreamPattern pattern)
    : Projector(orders, pattern), IProjectorHandler<ValueChanged>
{
    public ValueTask<Result> HandleAsync(IProjectorContext<ValueChanged> context, CancellationToken ct) => ValueTask.FromResult(Result.Success);
}
