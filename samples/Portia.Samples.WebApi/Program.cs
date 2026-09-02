using System.Security.Claims;
using Cntryl.Portia;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOpenApi();
builder.Services.AddSingleton<IPermissionEvaluator, InMemoryPermissionEvaluator>();
builder.Services.AddSingleton<IRequestQueuePublisher, InMemoryRequestQueuePublisher>();
_ = builder.Services.AddPortiaGeneratedComponents();

var app = builder.Build();
app.MapOpenApi();

// Sample-only stand-in for real authentication: reads an "X-Debug-Permission" header and attaches
// it as a claim, so the [RequiresPermission] check below has something to evaluate without wiring
// up a real JWT issuer just to demonstrate the mechanism.
app.Use(async (context, next) =>
{
    if (context.Request.Headers.TryGetValue("X-Debug-Permission", out var permission) && permission.Count > 0)
    {
        var identity = new ClaimsIdentity([new Claim("permission", permission[0]!)], authenticationType: "Debug");
        context.User = new ClaimsPrincipal(identity);
    }

    await next(context);
});

app.MapPortiaPost<CreateUser, Uuid>("/users");
app.MapPortiaPost<Ping>("/ping");
app.MapPortiaPost<SendWelcomeEmail>("/users/{user_id}/welcome");
app.MapPortiaGet<GetUser, string>("/users/{user_id}");
app.MapPortiaGetStream<ListUsers, string>("/users");
app.MapPortiaGetSse<ListUsers, string>("/users/events");
app.MapPortiaPost<CreateOrder, Uuid>("/orders");

app.Run();

// Exercises coarse-grained authorization: [RequiresPermission] declares the permission shape,
// checked automatically by the generated request bus (see InMemoryPermissionEvaluator below)
// before CreateUserHandler ever runs — the request and handler know nothing about the check.
[RequestRoute(realm: "*", area: "identity", resource: "users", operation: "create")]
[RequiresPermission("users:create")]
public sealed record CreateUser(string Email, string DisplayName) : IRequest<Uuid>, ICallable;

// A trivial IPermissionEvaluator: a real one would consult roles/claims/a policy store. This one
// just checks for a claim matching the required permission, to keep the sample self-contained.
sealed class InMemoryPermissionEvaluator : IPermissionEvaluator
{
    public ValueTask<Result> EvaluateAsync(ClaimsPrincipal actor, string permission, CancellationToken ct = default) =>
        ValueTask.FromResult(actor.HasClaim("permission", permission)
            ? Result.Success
            : Result.Failure(new RequestError(RequestErrorKind.Forbidden, $"Missing permission '{permission}'.")));
}

sealed class CreateUserHandler : IRequestHandler<CreateUser, Uuid>
{
    public ValueTask<Result<Uuid>> HandleAsync(IRequestContext<CreateUser> context, CancellationToken ct)
    {
        var request = context.Request;

        return string.IsNullOrWhiteSpace(request.Email)
            ? ValueTask.FromResult(Result<Uuid>.Failure(new RequestError(RequestErrorKind.Validation, "Email is required.")))
            : ValueTask.FromResult(Result<Uuid>.Success(Uuid.CreateVersion7()));
    }
}

[RequestRoute(realm: "*", area: "diagnostics", resource: "ping", operation: "ping")]
public sealed record Ping : IRequest, ICallable;

sealed class PingHandler : IRequestHandler<Ping>
{
    public ValueTask<Result> HandleAsync(IRequestContext<Ping> context, CancellationToken ct) =>
        ValueTask.FromResult(Result.Success);
}

// Exercises the Prefer-header pivot: no separate "Async" method — MapPortiaPost above notices
// SendWelcomeEmail also implements IQueuable and automatically enqueues instead of dispatching
// synchronously whenever the caller sends "Prefer: respond-async".
[RequestRoute(realm: "*", area: "identity", resource: "users", operation: "welcome")]
public sealed record SendWelcomeEmail(Uuid UserId) : IRequest, ICallable, IQueuable;

sealed class SendWelcomeEmailHandler : IRequestHandler<SendWelcomeEmail>
{
    public ValueTask<Result> HandleAsync(IRequestContext<SendWelcomeEmail> context, CancellationToken ct) =>
        ValueTask.FromResult(Result.Success);
}

// A trivial in-memory IRequestQueuePublisher, just so the sample can demonstrate the pivot
// without wiring up a real Fitz queue.
sealed class InMemoryRequestQueuePublisher : IRequestQueuePublisher
{
    public ValueTask EnqueueAsync<TRequest>(TRequest request, RequestRouteValues routeValues, string? actorToken, CancellationToken ct = default)
        where TRequest : IRequest, IQueuable
    {
        Console.WriteLine($"[queue] enqueued {typeof(TRequest).Name} (actor token present: {actorToken is not null})");
        return ValueTask.CompletedTask;
    }
}

// Exercises the generated HTTP binding: UserId matches the "{user_id}" route token, so it binds
// from the route; IncludeArchived doesn't, so on this GET it falls back to the query string. No
// attributes, no partial, no ASP.NET Core reference needed on this type at all — the generator
// intercepts the MapPortiaGet call site below and resolves this shape purely from its metadata.
//
// Also exercises permission-string interpolation: "{UserId}" here isn't a literal — the bus
// generator matches it against GetUser's own UserId property and substitutes the real value
// (`typed.UserId`) at dispatch time, so the permission checked is e.g. "users:<the-actual-id>:read".
[RequestRoute(realm: "*", area: "identity", resource: "users", operation: "get")]
[RequiresPermission("users:{UserId}:read")]
public sealed record GetUser(Uuid UserId, bool IncludeArchived) : IRequest<string>, ICallable;

sealed class GetUserHandler : IRequestHandler<GetUser, string>
{
    public ValueTask<Result<string>> HandleAsync(IRequestContext<GetUser> context, CancellationToken ct) =>
        ValueTask.FromResult(Result<string>.Success(
            $"{context.Request.UserId} (archived: {context.Request.IncludeArchived})"));
}

// Exercises streaming: mapped twice — once as an incrementally-flushed JSON array
// (MapPortiaGetStream) and once as Server-Sent Events (MapPortiaGetSse) — both driven by the
// exact same IStreamRequestHandler, proving both come from the same IAsyncEnumerable<T> source.
[RequestRoute(realm: "*", area: "identity", resource: "users", operation: "list")]
public sealed record ListUsers : IStreamRequest<string>, ICallable;

sealed class ListUsersHandler : IStreamRequestHandler<ListUsers, string>
{
    public async IAsyncEnumerable<string> HandleAsync(
        IRequestContext<ListUsers> context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        for (var i = 1; i <= 3; i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(200), ct);
            yield return $"user-{i}";
        }
    }
}

// Exercises the JSON-polish gap: Lines has no TryParse, so the generator falls back to a normal
// JsonElement.Deserialize<List<OrderLine>>() for this one property instead of the TryParse
// convention — the same mechanism handles any nested object or collection shape, not just lists.
public sealed record OrderLine(string Sku, int Quantity);

[RequestRoute(realm: "*", area: "orders", resource: "orders", operation: "create")]
public sealed record CreateOrder(List<OrderLine> Lines) : IRequest<Uuid>, ICallable;

sealed class CreateOrderHandler : IRequestHandler<CreateOrder, Uuid>
{
    public ValueTask<Result<Uuid>> HandleAsync(IRequestContext<CreateOrder> context, CancellationToken ct) =>
        ValueTask.FromResult(context.Request.Lines.Count > 0
            ? Result<Uuid>.Success(Uuid.CreateVersion7())
            : Result<Uuid>.Failure(new RequestError(RequestErrorKind.Validation, "At least one line is required.")));
}
