# Shared application setup

The application is configured once in a shared project. The HTTP deployment and
worker deployment call that same setup; only the worker calls `AddWorker()`.
These APIs are implemented in the current source checkout.

## Shared application project

Reference `Portia.Fitz` and `Portia.Generators` (with `PrivateAssets="all"`). Fitz
brings the core and dependency-injection packages. Each assembly declaring a module
or generated component needs the generator reference. Applications without Fitz can
reference `Portia.DependencyInjection` directly.

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

        return services.AddPortia(portia =>
        {
            portia.AddModule<AccountsModule>();
            portia.AddFitz(configuration.GetSection("Fitz"), fitz =>
            {
                fitz.AddEventStore();
                fitz.AddRequestClients();
                fitz.AddRpcServer();
                fitz.AddQueueWorker("queue://consumer/business/*");
            });
        });
    }
}
```

`AddEventStore()` supplies event serialization and the same store instance through
`IEventStore`, `IDomainEventReader`, and `IDomainEventWriter`. `AddRequestClients()`
supplies outbound RPC, queue, notice, and scheduling adapters and their serializers.
`AddRpcServer()` and `AddQueueWorker()` **declare** listeners; they do not start in
the API host. Multiple distinct queue routes create independent consumers. Repeating
an identical listener declaration has no additional effect.

Configuration can come from appsettings or normal .NET environment overrides:

```json
{
  "Fitz": {
    "Endpoint": "ws://localhost:4090/ws",
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

Reference the shared project, `Portia.AspNetCore`, and `Portia.Generators` with
`PrivateAssets="all"`. The installed generator package supplies the interceptor
namespace; the host does not need a manual project property.

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

## Worker deployment

Use the standard .NET worker host, referencing the shared project:

```csharp
var builder = Host.CreateApplicationBuilder(args);
builder.Services
    .AddAccountsApplication(builder.Configuration)
    .AddWorker();

await builder.Build().RunAsync();
```

`AddWorker()` activates the declarations once. Declarations added after activation
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

Declare distributed components alongside the other shared Fitz capabilities:

```csharp
fitz.UseFleet(new FleetRunOptions
{
    MembershipSelector = "lease://accounts/workers/*"
});
fitz.AddProjector<AccountProjector>("lease://accounts/components/account-projection");
fitz.AddReactor<AccountReactor>("lease://accounts/components/account-reactor");
```

Register the concrete components through their module, the application's
`IProjectionTarget<T>` implementations, and a durable `IProjectionCheckpointStore`
for reactors in the shared setup. A worker fails startup if required component
registrations, targets, checkpoints, or fleet configuration are missing. Modules and
component declarations can be registered in either order.

Each explicit lease route is a unit of independently assignable work. Replicas use
the existing fleet membership, assignment, and lease runner; membership changes
cancel revoked work and transfer ownership. Each pass has a fresh scope and reloads
its durable checkpoint. The framework cannot split one component's event pattern
into more partitions automatically: declare independently checkpointed components
or use the existing partition workload API when finer distribution is required.

Scoped projection targets and reactor dependencies can inject `WorkerLeaseContext`
to access the current route and `Authority.FencingToken`. Durable writes must enforce
that fence against expired holders; a lease and cancellation alone cannot prevent a
stalled process from attempting a late write. Rebuild data promotion remains owned
by the application.

Use the same membership selector, component lease routes, and component definitions
across replicas. Keep membership and component leases in separate areas. Reactor
and projector passes retain their existing delivery and checkpoint semantics; this
API does not promise exactly-once external effects.

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

The existing low-level hosting APIs remain supported. Do not also start the same
component or listener through them. Duplicate component hosting is rejected before
connection. Arbitrary custom or low-level transport listeners remain explicitly
application-owned and cannot be inferred or deduplicated by the shared builder.
