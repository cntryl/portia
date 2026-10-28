using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

/// <summary>
///     Verifies <see cref="PortiaBuilder.AddEventSourcedTenantDirectory{TStartEvent, TStopEvent}" /> composes the
///     event-sourced directory from what the host already registers, so an application names its lifecycle
///     events once instead of constructing the directory by hand.
/// </summary>
public sealed class EventSourcedTenantDirectoryRegistrationTests
{
    static readonly EventStreamPattern Registry = EventStreamPattern.ForPattern("global", "tenants", "registry");

    /// <summary>The directory reads tenant lifecycle events through the registered event reader.</summary>
    [Fact]
    public async Task ShouldReadTenantsThroughRegisteredReader()
    {
        var events = new InMemoryEventStore();
        await RegisterAsync(events, "acme");
        var services = new ServiceCollection();
        _ = services.AddSingleton<IDomainEventReader>(events);
        _ = services.AddPortia().AddEventSourcedTenantDirectory<TenantRegistered, TenantDeregistered>(Registry, GetTenantId);
        await using var provider = services.BuildServiceProvider();

        var directory = provider.GetRequiredService<ITenantDirectory>();

        _ = Assert.IsType<EventSourcedTenantDirectory<TenantRegistered, TenantDeregistered>>(directory);
        Assert.Equal([new TenantId("acme")], await directory.GetActiveTenantsAsync().ToListAsync());
    }

    /// <summary>A registered snapshot store is used, so a restart resumes from the saved roster.</summary>
    [Fact]
    public async Task ShouldSnapshotThroughRegisteredStore()
    {
        var events = new InMemoryEventStore();
        await RegisterAsync(events, "acme");
        var snapshots = new FitzKvTenantDirectorySnapshotStore(new FakeKvClient(), "kv://global/tenants/snapshots");
        var services = new ServiceCollection();
        _ = services.AddSingleton<IDomainEventReader>(events);
        _ = services.AddSingleton<ITenantDirectorySnapshotStore>(snapshots);
        _ = services.AddPortia().AddEventSourcedTenantDirectory<TenantRegistered, TenantDeregistered>(Registry, GetTenantId);
        await using var provider = services.BuildServiceProvider();

        _ = await provider.GetRequiredService<ITenantDirectory>().GetActiveTenantsAsync().ToListAsync();

        var snapshot = await snapshots.LoadAsync(Registry);
        Assert.NotNull(snapshot);
        Assert.Equal([new TenantId("acme")], snapshot.ActiveTenants);
    }

    /// <summary>A directory the application registered itself wins over the event-sourced one.</summary>
    [Fact]
    public void ShouldKeepApplicationRegisteredDirectory()
    {
        var fixedDirectory = new FixedTenantDirectory(new TenantId("demo"));
        var services = new ServiceCollection();
        _ = services.AddSingleton<ITenantDirectory>(fixedDirectory);
        _ = services.AddPortia().AddEventSourcedTenantDirectory<TenantRegistered, TenantDeregistered>(Registry, GetTenantId);
        using var provider = services.BuildServiceProvider();

        Assert.Same(fixedDirectory, provider.GetRequiredService<ITenantDirectory>());
    }

    static async Task RegisterAsync(InMemoryEventStore events, string tenant)
    {
        var ev = new TenantRegistered(tenant);
        ev.AttachMetadata(new DomainEventMetadata(Uuid.CreateVersion4(), Uuid.CreateVersion4(), 1, DateTimeOffset.UtcNow));
        await events.AppendAsync(new EventStreamAddress("global", "tenants", "registry"), 0, [ev]);
    }

    static TenantId GetTenantId(DomainEvent ev) => ev switch
    {
        TenantRegistered registered => new TenantId(registered.TenantId),
        TenantDeregistered deregistered => new TenantId(deregistered.TenantId),
        _ => throw new InvalidOperationException($"Unexpected event type '{ev.GetType()}'.")
    };
}
