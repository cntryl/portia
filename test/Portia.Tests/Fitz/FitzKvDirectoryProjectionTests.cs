using System.Text.Json.Serialization;
using Cntryl.Fitz.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

/// <summary>
///     Runs the paged team directory from <c>docs/projectors-and-reactors.md</c> end to end against a
///     real broker: team events appended to Fitz streams, the documented registrations, Portia's own
///     projector pass per tenant, and the query side paging a <see cref="KvDirectory{T, TKey}" />
///     through <see cref="FitzKvProjectionStore.BeginReadAsync" />. The event, repository, and
///     projector types here are the guide's, so the guide cannot drift from what actually works.
/// </summary>
[Collection(FitzBrokerCollectionDefinition.Name)]
[Trait("Category", "BrokerIntegration")]
public sealed class FitzKvDirectoryProjectionTests(FitzBrokerFixture broker)
{
    readonly FitzBrokerFixture _broker = broker;

    // Every run writes fresh tenants, streams, and projection resources on the shared broker.
    readonly string _route = "kv://portia-integration/teams/" + Uuid.CreateVersion4();
    readonly TenantId _tenantA = new("t" + Uuid.CreateVersion4().ToGuid().ToString("N"));
    readonly TenantId _tenantB = new("t" + Uuid.CreateVersion4().ToGuid().ToString("N"));

    /// <summary>
    ///     A tenant's list pages its projected teams in name order, reflects renames, and continues
    ///     from its cursor; another tenant's list and lookups see only that tenant's teams.
    /// </summary>
    [Fact]
    public async Task ShouldPageEachTenantsProjectedTeamsInNameOrder()
    {
        await using var client = await _broker.CreateClientAsync();
        var events = EventStore(client);
        Uuid alpha = Uuid.CreateVersion4(), charlie = Uuid.CreateVersion4(), delta = Uuid.CreateVersion4();
        var other = Uuid.CreateVersion4();
        await AppendAsync(events, _tenantA, charlie, new TeamCreated("Charlie"));
        await AppendAsync(events, _tenantA, alpha, new TeamCreated("alpha"));
        await AppendAsync(events, _tenantA, delta, new TeamCreated("Delta"), new TeamRenamed("Bravo"));
        await AppendAsync(events, _tenantB, other, new TeamCreated("Other"));
        await using var services = Services(client.Kv, events);
        await ProjectAsync(services, _tenantA);
        await ProjectAsync(services, _tenantB);
        await using var scope = services.CreateAsyncScope();
        var teams = scope.ServiceProvider.GetRequiredService<ITeamDirectory>();

        var first = await teams.ListAsync(_tenantA, take: 2, cursor: null, default);
        var second = await teams.ListAsync(_tenantA, take: 2, first.NextCursor, default);
        var tenantB = await teams.ListAsync(_tenantB, take: 2, cursor: null, default);

        Assert.Equal(["alpha", "Bravo"], first.Items.Select(static team => team.Name));
        Assert.Equal(["Charlie"], second.Items.Select(static team => team.Name));
        Assert.Null(second.NextCursor);
        Assert.Equal([new Team(other, "Other")], tenantB.Items);
        Assert.Equal(new Team(delta, "Bravo"), await teams.GetAsync(_tenantA, delta, default));
        Assert.Null(await teams.GetAsync(_tenantB, delta, default));
    }

    /// <summary>
    ///     A cursor names a position in one tenant's resource, so handing it to another tenant's list is
    ///     rejected rather than silently starting that tenant's page from a foreign key.
    /// </summary>
    [Fact]
    public async Task ShouldRejectOneTenantsCursorOnAnotherTenantsList()
    {
        await using var client = await _broker.CreateClientAsync();
        var events = EventStore(client);
        await AppendAsync(events, _tenantA, Uuid.CreateVersion4(), new TeamCreated("alpha"));
        await AppendAsync(events, _tenantA, Uuid.CreateVersion4(), new TeamCreated("Bravo"));
        await using var services = Services(client.Kv, events);
        await ProjectAsync(services, _tenantA);
        await using var scope = services.CreateAsyncScope();
        var teams = scope.ServiceProvider.GetRequiredService<ITeamDirectory>();
        var first = await teams.ListAsync(_tenantA, take: 1, cursor: null, default);

        var error = await Assert.ThrowsAsync<KvDirectoryQueryException>(async () =>
            await teams.ListAsync(_tenantB, take: 1, first.NextCursor, default));

        Assert.Equal(KvDirectoryQueryError.CursorMismatch, error.Kind);
    }

    // The guide's registrations, with the broker's event store standing in for AddFitz's.
    ServiceProvider Services(IKvClient kv, FitzEventStore events)
    {
        var services = new ServiceCollection();
        _ = services.AddSingleton<IDomainEventReader>(events);
        _ = services.AddScoped(_ => new TeamRepository(kv, _route));
        _ = services.AddScoped<ITeamDirectory>(sp => sp.GetRequiredService<TeamRepository>());
        _ = services.AddPortia().AddProjector<TeamProjector>(TeamRepository.Projector, WorkloadScope.PerTenant);
        return services.BuildServiceProvider();
    }

    // One hosted pass for one tenant: the workload scope the host would create, then the registered
    // projector's own pass, which binds the tenant, loads the checkpoint, and runs Portia's runner.
    static async Task ProjectAsync(ServiceProvider services, TenantId tenant)
    {
        await using var scope = services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<WorkloadContext>()
            .Initialize(new WorkloadIdentity(TeamRepository.Projector, tenant), TeamRepository.Projector);
        await services.GetRequiredService<ProjectorRegistration>().RunPass(scope.ServiceProvider, null, default);
    }

    static FitzEventStore EventStore(Client client) => new(client.Stream, TestJson.DomainSerializer(
        new DomainEventTypeCatalog().Register<TeamCreated>(1, "team.created").Register<TeamRenamed>(1, "team.renamed")));

    static async Task AppendAsync(FitzEventStore events, TenantId tenant, Uuid team, params DomainEvent[] changes)
    {
        for (var i = 0; i < changes.Length; i++)
            changes[i].AttachMetadata(new DomainEventMetadata(Uuid.CreateVersion4(), team, (ulong)i + 1,
                DateTimeOffset.UtcNow));
        await events.AppendAsync(new EventStreamAddress(tenant.Value, "teams", team.ToString()), 0, changes);
    }
}

// The repository from the guide, with the base route injected so each run writes its own resources.
sealed class TeamRepository(IKvClient kv, string route)
    : FitzKvProjectionStore(kv, route, Projector), ITeamDirectory
{
    public const string Projector = "TeamProjector";

    static readonly KvDirectoryIndex<Team> ByName = new(
        "by_name", 1, static team => [team.Name.ToUpperInvariant()]);

    static readonly KvDirectory<Team, Uuid> Teams = new(
        "teams", TeamJsonContext.Default.Team, static team => team.Id, static id => [id.ToGuid()], [ByName]);

    public ValueTask AddAsync(Team team, CancellationToken ct) => Teams.InsertAsync(Transaction, team, ct);

    public async ValueTask RenameAsync(Uuid id, string name, CancellationToken ct)
    {
        var current = await Teams.GetAsync(Transaction, id, ct)
            ?? throw new InvalidOperationException($"Team '{id}' was renamed before it was created.");
        await Teams.ReplaceAsync(Transaction, current, current with { Name = name }, ct);
    }

    public async ValueTask<Team?> GetAsync(TenantId tenant, Uuid id, CancellationToken ct)
    {
        await using var tx = await BeginReadAsync(tenant.Value, ct);
        return await Teams.GetAsync(tx, id, ct);
    }

    public async ValueTask<Page<Team>> ListAsync(TenantId tenant, int take, string? cursor, CancellationToken ct)
    {
        await using var tx = await BeginReadAsync(tenant.Value, ct);
        return await Teams.QueryAsync(tx, ByName.Query().Take(take).After(cursor), ct);
    }
}

sealed partial class TeamProjector(TeamRepository teams)
    : BatchProjector(teams, EventStreamPattern.ForTenant("teams")),
      IProjectorHandler<TeamCreated>, IProjectorHandler<TeamRenamed>
{
    public ValueTask HandleAsync(TeamCreated ev, IProjectorContext context, CancellationToken ct)
        => teams.AddAsync(new Team(ev.Metadata.AggregateId, ev.Name), ct);

    public ValueTask HandleAsync(TeamRenamed ev, IProjectorContext context, CancellationToken ct)
        => teams.RenameAsync(ev.Metadata.AggregateId, ev.Name, ct);
}

/// <summary>A team was created with a display name.</summary>
/// <param name="Name">The team's display name.</param>
[Discriminator("team.created")]
public sealed record TeamCreated(string Name) : DomainEvent;

/// <summary>A team's display name changed.</summary>
/// <param name="Name">The team's new display name.</param>
[Discriminator("team.renamed")]
public sealed record TeamRenamed(string Name) : DomainEvent;

/// <summary>One team as the team directory projection stores it.</summary>
/// <param name="Id">The team's identity.</param>
/// <param name="Name">The team's display name.</param>
public sealed record Team(Uuid Id, string Name);

/// <summary>The query side of the team directory.</summary>
public interface ITeamDirectory
{
    /// <summary>Gets one tenant's team, or <see langword="null" /> when the tenant has none by that ID.</summary>
    /// <param name="tenant">The tenant whose directory is read.</param>
    /// <param name="id">The team's identity.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>The team, or <see langword="null" />.</returns>
    ValueTask<Team?> GetAsync(TenantId tenant, Uuid id, CancellationToken ct);

    /// <summary>Lists one page of a tenant's teams in name order.</summary>
    /// <param name="tenant">The tenant whose directory is read.</param>
    /// <param name="take">The page size.</param>
    /// <param name="cursor">The previous page's cursor, or <see langword="null" /> for the first page.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>The page and the cursor for the next one.</returns>
    ValueTask<Page<Team>> ListAsync(TenantId tenant, int take, string? cursor, CancellationToken ct);
}

[JsonSerializable(typeof(Team))]
sealed partial class TeamJsonContext : JsonSerializerContext;
