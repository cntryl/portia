using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

/// <summary>
/// Exercises <c>RequestHttpBindingGenerator</c>'s generated interceptors end to end through a
/// real ASP.NET Core <see cref="TestServer" /> — an actual HTTP request/response round trip, not
/// an assertion on generated source text. Nothing in this repository tested this path before:
/// every gap here (most notably <see cref="ShouldReturnBadRequestWhenNestedBodyPropertyIsMalformed" />,
/// a regression test for a bug that previously only surfaced by hand-curling a running instance
/// of the sample app) had no automated coverage at all.
/// </summary>
public sealed class HttpBindingTests : IAsyncDisposable
{
    WebApplication? _app;

    /// <summary>
    /// Verifies that a route token binds from the route and a non-matching parameter falls back
    /// to the query string, on the same GET request.
    /// </summary>
    [Fact]
    public async Task ShouldBindRouteTokenAndFallBackToQueryString()
    {
        var client = await StartAsync(app => app.MapPortiaGet<HttpGetWidget, string>("/widgets/{widget_id}"));
        var widgetId = Uuid.CreateVersion7();

        var response = await client.GetAsync($"/widgets/{widgetId}?include_archived=true");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<string>();
        Assert.Equal($"{widgetId} (archived: True)", body);
    }

    /// <summary>
    /// Verifies that a route value failing its type's <c>TryParse</c> convention (an
    /// unparseable Uuid) is a 400, not an unhandled exception.
    /// </summary>
    [Fact]
    public async Task ShouldReturnBadRequestWhenRouteValueFailsTryParse()
    {
        var client = await StartAsync(app => app.MapPortiaGet<HttpGetWidget, string>("/widgets/{widget_id}"));

        var response = await client.GetAsync("/widgets/not-a-uuid?include_archived=true");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// Verifies that a nested collection body property (no <c>TryParse</c> convention available)
    /// binds correctly via the generator's JSON-fallback path.
    /// </summary>
    [Fact]
    public async Task ShouldBindNestedCollectionBodyProperty()
    {
        var client = await StartAsync(app => app.MapPortiaPost<HttpCreateOrder, Uuid>("/orders"));

        var response = await client.PostAsJsonAsync("/orders", new
        {
            lines = new[] { new { sku = "ABC", quantity = 2 } },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var orderId = await response.Content.ReadFromJsonAsync<Uuid>();
        Assert.NotEqual(Uuid.Empty, orderId);
    }

    /// <summary>
    /// Regression test: a nested body property whose JSON shape doesn't match its target type
    /// (a string where an array was expected) must be a 400, not an unhandled 500 — this exact
    /// case previously threw an uncaught <see cref="JsonException" /> straight through to the
    /// client, discovered only by hand-curling a running instance of the sample app.
    /// </summary>
    [Fact]
    public async Task ShouldReturnBadRequestWhenNestedBodyPropertyIsMalformed()
    {
        var client = await StartAsync(app => app.MapPortiaPost<HttpCreateOrder, Uuid>("/orders"));

        var response = await client.PostAsync(
            "/orders",
            new StringContent(/*lang=json,strict*/ """{"lines":"not-an-array"}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// Verifies that malformed top-level JSON (not just a malformed nested property) is a 400.
    /// </summary>
    [Fact]
    public async Task ShouldReturnBadRequestWhenTopLevelBodyIsNotValidJson()
    {
        var client = await StartAsync(app => app.MapPortiaPost<HttpCreateOrder, Uuid>("/orders"));

        var response = await client.PostAsync(
            "/orders",
            new StringContent("{not json at all", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// Verifies the <c>Prefer: respond-async</c> pivot: a request that's also
    /// <see cref="IQueuable" /> is enqueued and answered with 202 Accepted instead of dispatched
    /// synchronously, with no separate "Async"-suffixed endpoint to opt into it.
    /// </summary>
    [Fact]
    public async Task ShouldPivotToQueueWhenPreferRespondAsyncHeaderIsSent()
    {
        var publisher = new RecordingRequestQueuePublisher();
        var client = await StartAsync(
            app => app.MapPortiaPost<HttpSendPing>("/ping"),
            services => services.AddSingleton<IRequestQueuePublisher>(publisher));

        using var request = new HttpRequestMessage(HttpMethod.Post, "/ping")
        {
            Content = JsonContent.Create(new { }),
        };
        request.Headers.Add("Prefer", "respond-async");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        _ = Assert.Single(publisher.Enqueued);
        _ = Assert.IsType<HttpSendPing>(publisher.Enqueued[0]);
    }

    /// <summary>
    /// Verifies that the same endpoint dispatches synchronously — never touching the queue —
    /// when the caller doesn't send the <c>Prefer: respond-async</c> header.
    /// </summary>
    [Fact]
    public async Task ShouldDispatchSynchronouslyWithoutPreferHeader()
    {
        var publisher = new RecordingRequestQueuePublisher();
        var client = await StartAsync(
            app => app.MapPortiaPost<HttpSendPing>("/ping"),
            services => services.AddSingleton<IRequestQueuePublisher>(publisher));

        var response = await client.PostAsJsonAsync("/ping", new { });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(publisher.Enqueued);
    }

    /// <summary>
    /// Verifies that <see cref="RequiresPermissionAttribute" /> is enforced through the real HTTP
    /// pipeline — a caller lacking the permission gets 403, never reaching the handler.
    /// </summary>
    [Fact]
    public async Task ShouldReturnForbiddenWhenCallerLacksRequiredPermission()
    {
        var client = await StartAsync(app => app.MapPortiaPost<HttpGuardedAction>("/guarded"));

        var response = await client.PostAsJsonAsync("/guarded", new { });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// Verifies that a caller who does carry the required permission claim reaches the handler.
    /// </summary>
    [Fact]
    public async Task ShouldSucceedWhenCallerHasRequiredPermission()
    {
        var client = await StartAsync(app => app.MapPortiaPost<HttpGuardedAction>("/guarded"));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/guarded")
        {
            Content = JsonContent.Create(new { }),
        };
        request.Headers.Add("X-Debug-Permission", "http:guarded");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    /// <summary>
    /// Closes the last named, previously-unverified gap: a nested body property whose type is a
    /// value type (a <c>record struct</c>, no <c>TryParse</c>) rather than a reference type.
    /// <c>JsonElement.Deserialize&lt;TValue&gt;()</c> returns <c>TValue?</c>, i.e.
    /// <see cref="Nullable{T}" /> for a value type — this only ever verified working for
    /// reference-type nested shapes (<see cref="HttpOrderLine" />'s <see cref="List{T}" />)
    /// before now.
    /// </summary>
    [Fact]
    public async Task ShouldBindValueTypeNestedBodyProperty()
    {
        var client = await StartAsync(app => app.MapPortiaPost<HttpCreatePayment, Uuid>("/payments"));

        var response = await client.PostAsJsonAsync("/payments", new { amount = new { currency = "USD", cents = 500 } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var paymentId = await response.Content.ReadFromJsonAsync<Uuid>();
        Assert.NotEqual(Uuid.Empty, paymentId);
    }

    /// <summary>
    /// Verifies streaming as an incrementally-flushed JSON array.
    /// </summary>
    [Fact]
    public async Task ShouldStreamAsJsonArray()
    {
        var client = await StartAsync(app => app.MapPortiaGetStream<HttpListWidgets, string>("/widgets"));

        var response = await client.GetAsync("/widgets");
        var items = await response.Content.ReadFromJsonAsync<List<string>>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["a", "b"], items);
    }

    /// <summary>
    /// Verifies streaming as Server-Sent Events, driven by the exact same
    /// <see cref="IStreamRequestHandler{TRequest,TOut}" /> as the JSON-array mapping above.
    /// </summary>
    [Fact]
    public async Task ShouldStreamAsServerSentEvents()
    {
        var client = await StartAsync(app => app.MapPortiaGetSse<HttpListWidgets, string>("/widgets/events"));

        var response = await client.GetAsync("/widgets/events");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("data: a", body, StringComparison.Ordinal);
        Assert.Contains("data: b", body, StringComparison.Ordinal);
    }

    async Task<HttpClient> StartAsync(Action<IEndpointRouteBuilder> map, Action<IServiceCollection>? configureServices = null)
    {
        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        _ = builder.Services.AddSingleton<IPermissionEvaluator, DebugHeaderPermissionEvaluator>();
        configureServices?.Invoke(builder.Services);
        _ = builder.Services.AddPortiaGeneratedComponents();

        _app = builder.Build();

        _ = _app.Use(async (context, next) =>
        {
            if (context.Request.Headers.TryGetValue("X-Debug-Permission", out var permission) && permission.Count > 0)
            {
                var identity = new ClaimsIdentity([new Claim("permission", permission[0]!)], authenticationType: "Debug");
                context.User = new ClaimsPrincipal(identity);
            }

            await next(context);
        });

        map(_app);

        await _app.StartAsync();
        return _app.GetTestClient();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
            await _app.DisposeAsync();
    }

    sealed class DebugHeaderPermissionEvaluator : IPermissionEvaluator
    {
        public ValueTask<Result> EvaluateAsync(ClaimsPrincipal actor, string permission, CancellationToken ct = default) =>
            ValueTask.FromResult(actor.HasClaim("permission", permission)
                ? Result.Success
                : Result.Failure(new RequestError(RequestErrorKind.Forbidden, $"Missing permission '{permission}'.")));
    }

    sealed class RecordingRequestQueuePublisher : IRequestQueuePublisher
    {
        public List<object> Enqueued { get; } = [];

        public ValueTask EnqueueAsync<TRequest>(TRequest request, RequestRouteValues routeValues, string? actorToken, CancellationToken ct = default)
            where TRequest : IRequest, IQueuable
        {
            Enqueued.Add(request);
            return ValueTask.CompletedTask;
        }
    }
}

[RequestRoute(realm: "*", area: "http-binding-tests", resource: "widgets", operation: "get")]
sealed record HttpGetWidget(Uuid WidgetId, bool IncludeArchived) : IRequest<string>, ICallable;

sealed class HttpGetWidgetHandler : IRequestHandler<HttpGetWidget, string>
{
    public ValueTask<Result<string>> HandleAsync(IRequestContext<HttpGetWidget> context, CancellationToken ct) =>
        ValueTask.FromResult(Result<string>.Success(
            $"{context.Request.WidgetId} (archived: {context.Request.IncludeArchived})"));
}

sealed record HttpOrderLine(string Sku, int Quantity);

readonly record struct HttpMoney(string Currency, int Cents);

[RequestRoute(realm: "*", area: "http-binding-tests", resource: "payments", operation: "create")]
sealed record HttpCreatePayment(HttpMoney Amount) : IRequest<Uuid>, ICallable;

sealed class HttpCreatePaymentHandler : IRequestHandler<HttpCreatePayment, Uuid>
{
    public ValueTask<Result<Uuid>> HandleAsync(IRequestContext<HttpCreatePayment> context, CancellationToken ct) =>
        ValueTask.FromResult(Result<Uuid>.Success(Uuid.CreateVersion7()));
}

[RequestRoute(realm: "*", area: "http-binding-tests", resource: "orders", operation: "create")]
sealed record HttpCreateOrder(List<HttpOrderLine> Lines) : IRequest<Uuid>, ICallable;

sealed class HttpCreateOrderHandler : IRequestHandler<HttpCreateOrder, Uuid>
{
    public ValueTask<Result<Uuid>> HandleAsync(IRequestContext<HttpCreateOrder> context, CancellationToken ct) =>
        ValueTask.FromResult(Result<Uuid>.Success(Uuid.CreateVersion7()));
}

[RequestRoute(realm: "*", area: "http-binding-tests", resource: "ping", operation: "ping")]
sealed record HttpSendPing : IRequest, ICallable, IQueuable;

sealed class HttpSendPingHandler : IRequestHandler<HttpSendPing>
{
    public ValueTask<Result> HandleAsync(IRequestContext<HttpSendPing> context, CancellationToken ct) =>
        ValueTask.FromResult(Result.Success);
}

[RequestRoute(realm: "*", area: "http-binding-tests", resource: "guarded", operation: "run")]
[RequiresPermission("http:guarded")]
sealed record HttpGuardedAction : IRequest, ICallable;

sealed class HttpGuardedActionHandler : IRequestHandler<HttpGuardedAction>
{
    public ValueTask<Result> HandleAsync(IRequestContext<HttpGuardedAction> context, CancellationToken ct) =>
        ValueTask.FromResult(Result.Success);
}

[RequestRoute(realm: "*", area: "http-binding-tests", resource: "widgets", operation: "list")]
sealed record HttpListWidgets : IStreamRequest<string>, ICallable;

sealed class HttpListWidgetsHandler : IStreamRequestHandler<HttpListWidgets, string>
{
    public async IAsyncEnumerable<string> HandleAsync(
        IRequestContext<HttpListWidgets> context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        yield return "a";
        await Task.Yield();
        yield return "b";
    }
}
