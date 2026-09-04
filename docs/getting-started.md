# Getting started

Portia targets .NET 10 and is split into capability-specific packages. Most HTTP applications
start with:

- `Portia.Abstractions` for requests, handlers, results, routing, and authorization contracts.
- `Portia.AspNetCore` for minimal API endpoint mapping.
- `Portia.Generators` for compile-time request-bus, registration, and HTTP-binding generation.
- `Portia.DependencyInjection` when background runners should be hosted by the application.
- `Portia.Fitz` when the application uses Fitz-backed RPC, queues, notices, schedules, streams,
  event storage, or fleet leases.
- `Portia.Jwt` when queued and scheduled requests carry JWT actor identities that must be
  revalidated when the work runs.

Portia packages use the cntryl GitHub Packages feed. Configure NuGet authentication for
`https://nuget.pkg.github.com/cntryl/index.json`, then add only the packages for the capabilities
the application uses.

## Define a request and handler

A request opts into each remote transport explicitly. `ICallable` allows synchronous remote
dispatch, including HTTP and Fitz RPC. The route describes its stable wire identity.

```csharp
using Cntryl.Portia;

[RequestRoute(realm: "*", area: "greetings", resource: "messages", operation: "create")]
public sealed record CreateGreeting(string Name) : IRequest<string>, ICallable;

sealed class CreateGreetingHandler : IRequestHandler<CreateGreeting, string>
{
    public ValueTask<Result<string>> HandleAsync(
        IRequestContext<CreateGreeting> context,
        CancellationToken ct) =>
        ValueTask.FromResult(Result<string>.Success($"Hello, {context.Request.Name}!"));
}
```

The handler contains application behavior only. It does not know whether the request arrived
over HTTP, Fitz RPC, a queue, or an in-process call.

## Register generated components

Reference `Portia.Generators` and enable its HTTP-binding interceptor namespace in the application
project:

```xml
<PropertyGroup>
  <InterceptorsNamespaces>$(InterceptorsNamespaces);Cntryl.Portia.Generated</InterceptorsNamespaces>
</PropertyGroup>
```

The generator discovers requests and handlers at compile time and emits
`AddPortiaGeneratedComponents()`. Call it during host construction, then map the request directly
as a minimal API endpoint:

```csharp
using Cntryl.Portia;

var builder = WebApplication.CreateBuilder(args);
_ = builder.Services.AddPortiaGeneratedComponents();

var app = builder.Build();
app.MapPortiaPost<CreateGreeting, string>("/greetings");
app.Run();
```

Primary-constructor parameters matching route tokens bind from the route. Remaining parameters
bind from the query string for GET and DELETE, or from the JSON body for POST, PUT, and PATCH.
Request types remain independent of ASP.NET Core attributes.

## Add authorization

Use `RequiresPermission` for coarse permission checks. Tokens in the permission string resolve
against non-nullable request properties and are validated by the generator.

```csharp
[RequestRoute(realm: "*", area: "orders", resource: "orders", operation: "get")]
[RequiresPermission("orders:{OrderId}:read")]
public sealed record GetOrder(Uuid OrderId) : IRequest<string>, ICallable;
```

Register an `IPermissionEvaluator` implementation in the application. For row-level rules, add an
`IRequestAuthorizer<TRequest>`; permission evaluation always runs first.

## Opt into asynchronous dispatch

Add `IQueuable` to a request that may be queued. The same POST, PUT, PATCH, or DELETE endpoint
returns `202 Accepted` and enqueues the request when the client sends `Prefer: respond-async`.
Without that header, the request uses the ordinary synchronous handler path.

```csharp
public sealed record SendWelcomeEmail(Uuid UserId) : IRequest, ICallable, IQueuable;
```

The application must register an `IRequestQueuePublisher`. A Fitz-backed application can use the
implementation from `Portia.Fitz`.

## Stream results

One `IStreamRequestHandler<TRequest, TOut>` can back either an incrementally flushed JSON array or
Server-Sent Events:

```csharp
app.MapPortiaGetStream<ListMessages, Message>("/messages");
app.MapPortiaGetSse<ListMessages, Message>("/messages/events");
```

Both mappings dispatch through the same generated request bus, so authorization, actor
propagation, and tracing stay consistent with non-streaming requests.

## Continue from the contracts

The root [README](../README.md) summarizes the package boundaries and runtime guarantees. Public
contracts in `Portia.Abstractions` are the authoritative API surface; the integration and unit
tests under `test/Portia.Tests` provide executable contract coverage for HTTP binding, all
transports, event storage, hosted runners, multi-tenancy, and fleet behavior.

Runnable end-to-end applications will be maintained in dedicated sample repositories rather than
inside Portia itself.
