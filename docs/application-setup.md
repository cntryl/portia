# Shared application setup

The application is configured once in a shared project. The HTTP deployment and
worker deployment call that same setup; only the worker calls `AddWorkers()`.
These APIs are implemented in the current source checkout.

## Shared application project

Reference `Portia.Fitz`; it brings the core and dependency-injection packages. The
dependency-injection package includes Portia's generator and compiler configuration.
Applications without Fitz can reference `Portia.DependencyInjection` directly.

```csharp
using Cntryl.Portia;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

public static class ApplicationSetup
{
    public static PortiaBuilder AddAccountsApplication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Application-specific dependencies are ordinary DI registrations.
        // ActorValidator implements IRequestActorValidator using the application's identity policy.
        services.AddScoped<IRequestActorValidator, ActorValidator>();

        services.AddScoped<IAccountRepository, AccountRepository>();
        services.AddScoped<IPlatformSummaryRepository, PlatformSummaryRepository>();
        services.AddSingleton<ITenantDirectory, AccountTenantDirectory>();

        return services.AddPortia()
            .AddRequestHandler<DepositAccountHandler>()
            .AddProjector<AccountProjector>(WorkloadScope.PerTenant)
            .AddProjector<PlatformSummaryProjector>(WorkloadScope.Global)
            .AddReactor<AccountReactor>(WorkloadScope.PerTenant)
            .AddFitz(configuration.GetSection("Fitz"));
    }
}
```

`AddFitz()` supplies one event store through `IEventStore`, `IDomainEventReader`,
and `IDomainEventWriter`, plus outbound RPC, queue, notice, and scheduling clients.
Portia's JSON event serializer is also available without Fitz.
By default, `AddFitz()` declares listeners for every transport exposed by selected handlers;
it does not start them in the API host. Multiple distinct queue routes create independent consumers.
Repeating an identical listener declaration has no additional effect. A callback that only configures
fleet membership retains the default listeners. The first transport-specific worker method narrows
the default and later methods add to that selection. Call `DisableRequestWorkers()` for a deployment
that hosts component workloads but no request listeners. Startup rejects any explicit transport
selector that matches no selected handler, before attempting to connect to Fitz.

Configuration can come from appsettings or normal .NET environment overrides:

```json
{
  "Fitz": {
    "Endpoint": "ws://localhost:4090/ws",
    "ApplicationName": "accounts",
    "StartupTimeoutSeconds": 15
  }
}
```

`Fitz__Endpoint`, `Fitz__StartupTimeoutSeconds`, and optionally `Fitz__Token` are the
corresponding environment names. The backend token authenticates the Fitz connection;
`IRequestActorValidator` separately validates the actor carried by a delivered request.
For rotating backend credentials, use the overload accepting Fitz's `ClientConfig`
and its `TokenProvider`. No permissive actor validator is registered automatically.

## API deployment

Reference the shared project and `Portia.AspNetCore`. `Portia.DependencyInjection` supplies
the generator transitively; the host does not need a separate analyzer reference or manual
compiler property.

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAccountsApplication(builder.Configuration);

var app = builder.Build();
app.MapPortiaPost<DepositAccount>("/accounts/{id}");
app.Run();
```

HTTP endpoints remain explicit and support the existing ASP.NET conventions. The API
can execute a command directly or publish work. Merely sharing the application
setup starts no queue, RPC, projector, or reactor worker. Configure HTTP authentication
and authorization using the application's normal ASP.NET setup.

Generated interception of `WebApplicationBuilder.Build()` (and `WebApplication.Create()`)
registers Microsoft's `v1` OpenAPI 3.1 document before the provider is built and maps
`/openapi/v1.json` and `/openapi/v1.yml` afterward. Registration and mapping are idempotent across
route groups and multiple Portia endpoints, add no hosted service, and leave `AddPortia()`
host-neutral. Security schemes are application-owned and are not inferred from authorization
metadata.

## Worker deployment

Use the standard .NET worker host, referencing the shared project:

```csharp
var builder = Host.CreateApplicationBuilder(args);
builder.Services
    .AddAccountsApplication(builder.Configuration)
    .AddWorkers();

await builder.Build().RunAsync();
```

`AddWorkers()` activates the declarations once. Declarations added after activation
are also included, provided all setup happens before the host is built. Applications
can declare other hosted services in shared setup with
`portia.ConfigureWorker("name", services => services.AddHostedService<MyWorker>())`.
Names must be unique; a different callback under the same name is rejected.

The API deployment can scale with HTTP demand, while worker replicas compete for
queue work and serve RPC. Infrastructure autoscaling configuration remains deployment-owned.
Notice subscriptions are fanout on each replica; scheduled delivery follows the
schedule's one/broadcast policy. Those transports are not interchangeable with a
competing queue.

## Construct and hydrate aggregates directly

```csharp
var account = await repository.HydrateAsync(new Account(id), ct);
account.Deposit(amount);
await repository.SaveAsync(account, context, ct);

// Later, reuse the same instance and read only events committed since its last position.
await repository.HydrateAsync(account, ct);
```

No aggregate factory registration or generated constructor wiring is needed.
The developer owns ordinary construction, including constructor dependencies.
`AddPortia()` registers the scoped repository once.

Hydration returns the same instance. It starts at `CommittedStreamPosition`, checks
contiguous offsets, identities, event versions, and duplicate event IDs, and applies
only raised events. A missing or caught-up stream leaves the instance unchanged.
Pending events or audits must be saved first; refresh cannot overwrite them. The
instance cannot be mutated, saved, or hydrated concurrently while a read is pending.
Read/validation failure does not apply the fetched batch. If a domain event handler
throws while applying the batch, discard the instance; arbitrary application state
cannot be rolled back by the framework.

## Projectors and reactors across worker replicas

Workload registration belongs to Portia and requires an explicit scope:

```csharp
portia.AddProjector<AccountProjector>(WorkloadScope.PerTenant);
portia.AddProjector<PlatformSummaryProjector>(WorkloadScope.Global);
portia.AddReactor<AccountReactor>(WorkloadScope.PerTenant);
```

`WorkloadScope.PerTenant` creates an independently owned workload for each active `ITenantDirectory`
entry. Portia replaces the component pattern's realm with the tenant ID, retaining its
area and resource filters. `WorkloadScope.Global` creates one logical workload and retains the
component's declared pattern. It does not grant cross-tenant access or scan every realm.

An omitted scope, both scopes, a conflicting registration, or duplicate workload name
fails during configuration. Repeating an identical registration is idempotent. The
component's full CLR type name is its default workload name, used to coordinate ownership.
That default does **not** rename the component: a projector's or reactor's own `Name` is its
checkpoint identity, and setting `options.Name` is what deliberately overrides it — so
declaring a workload never silently repoints existing checkpoints. Projector options also accept `Processing`
(`ProjectionRunOptions`, including a rebuild ID) and a positive `PollInterval`.

`AddWorkers()` runs every declared workload under one hosted service. With no
`IWorkloadCoordinator` registered it owns them all in this process, which is correct for a
single worker replica and needs no infrastructure at all; it logs a warning saying so. Register
a distributed coordinator before scaling workers past one replica.

Fitz implements `IWorkloadCoordinator` and consumes these same declarations. There is
no second component list or per-component lease route. `Fitz:ApplicationName` separates
applications sharing a broker; it defaults to `portia`. Keep it identical across the
application's replicas and distinct between independently deployed applications.
Optional `fitz.UseFleet(...)` configures membership timing and an explicit membership
selector. Workload lease resources are deterministic UUIDv5 values derived from the
workload name and optional tenant identity, in a separate area from membership leases.

Tenant additions become available for assignment. Removal cancels that tenant's work
at the next coordinator reconciliation; retained global and other tenant workloads
keep their ownership. Fleet membership changes transfer ownership between replicas.
Each owned workload pass receives a fresh DI scope and reloads its checkpoint.

Projectors derive from `BaseProjector` or `BaseBatchProjector` and pass their ordinary
repository into the base constructor. That repository implements `IProjectionStore`:
loading progress and opening an atomic unit of work. There is no separate target
registration and no repository lookup through projector context.

Reactors derive from `BaseReactor` or `BaseBatchReactor` and pass a constructor dependency
implementing `IProjectionCheckpointStore`. This can be the same application repository
they use for reactions; Portia does not require a separate global store registration.
Register `ITenantDirectory` when any workload is per tenant. Registration and infrastructure
setup can occur in either order, before building the host.

Scoped repositories and dependencies can inject infrastructure-neutral `WorkloadContext` for
the current `Identity` and `Tenant`. Fitz holds and renews the workload lease around the complete
projector or reactor run and cancels that run if the lease is lost.
`FleetRunOptions.PartitionStopTimeout` defaults to 30 seconds;
if revoked work ignores cancellation beyond it, Portia faults the runner and stops a hosted
application instead of starting replacement work beside stale work. Checkpoints use workload name, canonical event pattern,
and optional rebuild ID, keeping tenants and rebuild generations independent.
Reactions retain their system principal and causal event context. External reaction
effects remain at-least-once.

## Lifetimes and overrides

`AddFitz(...)` owns one long-lived client per host. Host startup awaits connection
with a bounded timeout before listeners start. Worker scopes and RPC registrations
are released on shutdown; host disposal releases the owned connection.

`UseFitzClient(existingClient, configure)` accepts an already connected client and
neither connects nor disposes it. This allows an application to own the connection
explicitly. Conflicting connection configurations are rejected.

Default serializers use `TryAdd`; normal DI registrations can supply overrides.
Request envelope serializers used by transport consumers must support singleton
use. Actor validation and request execution happen inside per-delivery scopes.

Low-level hosting APIs are available for manually managed runners. Do not also start the same
component or listener through them. Duplicate component hosting is rejected before
connection. Arbitrary custom or low-level transport listeners remain explicitly
application-owned and cannot be inferred or deduplicated by the shared builder.

See [projectors and reactors](projectors-and-reactors.md) for the four bases,
constructor contracts, and EF Core, ADO.NET, and DynamoDB implementation considerations.
