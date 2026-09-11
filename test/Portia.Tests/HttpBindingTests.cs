using System.Globalization;
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
///     Exercises <c>RequestHttpBindingGenerator</c>'s generated interceptors end to end through a
///     real ASP.NET Core <see cref="TestServer" /> — an actual HTTP request/response round trip, not
///     an assertion on generated source text. Nothing in this repository tested this path before:
///     every gap here (most notably <see cref="ShouldReturnBadRequestWhenNestedBodyPropertyIsMalformed" />,
///     a regression test for a bug that previously only surfaced during manual HTTP probing) had no
///     automated coverage at all.
/// </summary>
public sealed class HttpBindingTests : IAsyncDisposable
{
    WebApplication? _app;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    /// <summary>
    ///     Verifies that a route token binds from the route and a non-matching parameter falls back
    ///     to the query string, on the same GET request.
    /// </summary>
    [Fact]
    public async Task ShouldBindRouteTokenAndFallBackToQueryString()
    {
        var client = await StartAsync(app => app.MapPortiaGet<HttpGetWidget, string>("/widgets/{widget_id}"));
        var widgetId = Uuid.CreateVersion4();

        var response = await client.GetAsync($"/widgets/{widgetId}?include_archived=true");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<string>();
        Assert.Equal($"{widgetId} (archived: True)", body);
    }

    /// <summary>
    ///     Verifies that a route value failing its type's <c>TryParse</c> convention (an
    ///     unparseable Uuid) is a 400, not an unhandled exception.
    /// </summary>
    [Fact]
    public async Task ShouldReturnBadRequestWhenRouteValueFailsTryParse()
    {
        var client = await StartAsync(app => app.MapPortiaGet<HttpGetWidget, string>("/widgets/{widget_id}"));

        var response = await client.GetAsync("/widgets/not-a-uuid?include_archived=true");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    ///     Verifies that a nested collection body property (no <c>TryParse</c> convention available)
    ///     binds correctly via the generator's JSON-fallback path.
    /// </summary>
    [Fact]
    public async Task ShouldBindNestedCollectionBodyProperty()
    {
        var client = await StartAsync(app => app.MapPortiaPost<HttpCreateOrder, Uuid>("/orders"));

        var response = await client.PostAsJsonAsync("/orders", new
        {
            lines = new[] { new { sku = "ABC", quantity = 2 } }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var orderId = await response.Content.ReadFromJsonAsync<Uuid>();
        Assert.NotEqual(Uuid.Empty, orderId);
    }

    /// <summary>
    ///     Regression test: a nested body property whose JSON shape doesn't match its target type
    ///     (a string where an array was expected) must be a 400, not an unhandled 500 — this exact
    ///     case previously threw an uncaught <see cref="JsonException" /> straight through to the
    ///     client, discovered only through a manual HTTP request.
    /// </summary>
    [Fact]
    public async Task ShouldReturnBadRequestWhenNestedBodyPropertyIsMalformed()
    {
        var client = await StartAsync(app => app.MapPortiaPost<HttpCreateOrder, Uuid>("/orders"));

        var response = await client.PostAsync(
            "/orders",
            new StringContent( /*lang=json,strict*/ """{"lines":"not-an-array"}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    ///     Verifies that malformed top-level JSON (not just a malformed nested property) is a 400.
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
    ///     Verifies that a completely empty body on an endpoint that requires one is a 400, not an
    ///     unhandled exception — an empty stream isn't "malformed JSON" in the same sense as
    ///     truncated or garbled text, so it's worth checking <c>JsonDocument.ParseAsync</c> fails the
    ///     same clean way for it.
    /// </summary>
    [Fact]
    public async Task ShouldReturnBadRequestWhenBodyIsCompletelyEmpty()
    {
        var client = await StartAsync(app => app.MapPortiaPost<HttpCreateOrder, Uuid>("/orders"));

        var response = await client.PostAsync("/orders", new ByteArrayContent([]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    ///     Verifies the <c>Prefer: respond-async</c> pivot: a request that's also
    ///     <see cref="IQueuable" /> is enqueued and answered with 202 Accepted instead of dispatched
    ///     synchronously, with no separate "Async"-suffixed endpoint to opt into it.
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
            Content = JsonContent.Create(new { })
        };
        request.Headers.Add("Prefer", "respond-async");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("respond-async", response.Headers.GetValues("Preference-Applied").Single());
        using var receipt = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var requestId = receipt.RootElement.GetProperty("request_id").GetString();
        Assert.NotNull(requestId);
        Assert.NotEqual(Uuid.Empty, Uuid.Parse(requestId, CultureInfo.InvariantCulture));
        _ = Assert.Single(publisher.Enqueued);
        _ = Assert.IsType<HttpSendPing>(publisher.Enqueued[0]);
    }

    /// <summary>Preference names are matched as tokens rather than substrings.</summary>
    [Fact]
    public async Task ShouldDispatchSynchronouslyGivenSubstringWhenPreferenceIsNotExact()
    {
        var publisher = new RecordingRequestQueuePublisher();
        var client = await StartAsync(app => app.MapPortiaPost<HttpSendPing>("/ping"),
            services => services.AddSingleton<IRequestQueuePublisher>(publisher));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/ping") { Content = JsonContent.Create(new { }) };
        _ = request.Headers.TryAddWithoutValidation("Prefer", "x-respond-async");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(publisher.Enqueued);
    }

    /// <summary>Only a single nonempty Bearer credential can cross the queue boundary.</summary>
    [Fact]
    public async Task ShouldReturnProblemGivenMalformedAuthorizationWhenQueueing()
    {
        var client = await StartAsync(app => app.MapPortiaPost<HttpSendPing>("/ping"),
            services => services.AddSingleton<IRequestQueuePublisher>(new RecordingRequestQueuePublisher()));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/ping") { Content = JsonContent.Create(new { }) };
        _ = request.Headers.TryAddWithoutValidation("Prefer", "respond-async");
        _ = request.Headers.TryAddWithoutValidation("Authorization", "Basic secret");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(400, problem.GetProperty("status").GetInt32());
        Assert.Equal("Bad Request", problem.GetProperty("title").GetString());
        Assert.True(problem.TryGetProperty("detail", out _));
    }

    /// <summary>Optional HTTP whitespace is removed before validating the Bearer credential.</summary>
    [Fact]
    public async Task ShouldAcceptTrimmedCredentialGivenTrailingWhitespaceWhenQueueing()
    {
        var publisher = new RecordingRequestQueuePublisher();
        var client = await StartAsync(app => app.MapPortiaPost<HttpSendPing>("/ping"),
            services => services.AddSingleton<IRequestQueuePublisher>(publisher));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/ping") { Content = JsonContent.Create(new { }) };
        _ = request.Headers.TryAddWithoutValidation("Prefer", "respond-async");
        _ = request.Headers.TryAddWithoutValidation("Authorization", "Bearer abc ");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("abc", Assert.Single(publisher.ActorTokens));
    }

    /// <summary>An absent optional body binds as an empty object.</summary>
    [Fact]
    public async Task ShouldBindDefaultsGivenAbsentOptionalBodyWhenPosting()
    {
        var client = await StartAsync(app => app.MapPortiaPost<HttpOptionalBody, string>("/optional"));

        var response = await client.PostAsync("/optional", new ByteArrayContent([]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("fallback", await response.Content.ReadFromJsonAsync<string>());
    }

    /// <summary>JSON bodies are rejected before exceeding the configured bound.</summary>
    [Fact]
    public async Task ShouldReturnPayloadTooLargeGivenBodyExceedsConfiguredMaximumWhenPosting()
    {
        var client = await StartAsync(app => app.MapPortiaPost<HttpOptionalBody, string>("/optional"),
            services => services.Configure<PortiaHttpOptions>(options => options.MaxJsonBodyBytes = 8));

        var response = await client.PostAsync("/optional", new StringContent(
            /*lang=json,strict*/ """{"value":"too-long"}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>
    ///     Verifies that a body limit misconfigured to zero or below fails every request visibly as a
    ///     500 rather than silently lifting the limit, and that the response discloses nothing about the
    ///     cause — the same non-disclosing shape any unexpected failure at this boundary takes.
    /// </summary>
    /// <param name="maximum">The misconfigured body limit.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ShouldFailVisiblyWithoutDisclosureWhenBodyLimitIsMisconfigured(long maximum)
    {
        var client = await StartAsync(app => app.MapPortiaPost<HttpOptionalBody, string>("/optional"),
            services => services.Configure<PortiaHttpOptions>(options => options.MaxJsonBodyBytes = maximum));

        var response = await client.PostAsync("/optional", new StringContent(
            /*lang=json,strict*/ """{"value":"x"}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("An unexpected error occurred.", body, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(PortiaHttpOptions.MaxJsonBodyBytes), body, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Verifies that a body property whose casing differs from the configured naming policy still
    ///     binds. The generated binder looks for the exact name first and falls back to a case-insensitive
    ///     match, which is what lets a hand-written client that sends <c>Value</c> work against an
    ///     endpoint whose policy produced <c>value</c>.
    /// </summary>
    /// <param name="json">The request body, spelling the property some other way.</param>
    [Theory]
    [InlineData(/*lang=json,strict*/ """{"Value":"bound"}""")]
    [InlineData(/*lang=json,strict*/ """{"VALUE":"bound"}""")]
    public async Task ShouldBindBodyPropertyWhoseCasingDiffersFromTheNamingPolicy(string json)
    {
        var client = await StartAsync(app => app.MapPortiaPost<HttpOptionalBody, string>("/optional"));

        var response = await client.PostAsync("/optional",
            new StringContent(json, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"bound\"", await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    ///     Verifies that the same endpoint dispatches synchronously — never touching the queue —
    ///     when the caller doesn't send the <c>Prefer: respond-async</c> header.
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
    ///     Verifies that <see cref="RequiresPermissionAttribute" /> is enforced through the real HTTP
    ///     pipeline — a caller lacking the permission gets 403, never reaching the handler.
    /// </summary>
    [Fact]
    public async Task ShouldReturnForbiddenWhenCallerLacksRequiredPermission()
    {
        var client = await StartAsync(app => app.MapPortiaPost<HttpGuardedAction>("/guarded"));

        var response = await client.PostAsJsonAsync("/guarded", new { });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("false", response.Headers.GetValues(ResultHttpExtensions.TransientHeaderName).Single());
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("about:blank", problem.RootElement.GetProperty("type").GetString());
        Assert.Equal("Forbidden", problem.RootElement.GetProperty("title").GetString());
        Assert.Equal(403, problem.RootElement.GetProperty("status").GetInt32());
        Assert.Contains("http:guarded", problem.RootElement.GetProperty("detail").GetString(),
            StringComparison.Ordinal);
        Assert.Equal("/guarded", problem.RootElement.GetProperty("instance").GetString());
        Assert.False(problem.RootElement.GetProperty("transient").GetBoolean());
        Assert.False(problem.RootElement.TryGetProperty("message", out _));
    }

    /// <summary>A transient request failure remains distinguishable over HTTP.</summary>
    [Fact]
    public async Task ShouldExposeTransientRequestFailureInProblemAndHeader()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/conflict";
        context.Response.Body = new MemoryStream();
        var result = Result.Failure(new RequestError(RequestErrorKind.Conflict, "Try again.", true));

        await result.ToHttpResult().ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status409Conflict, context.Response.StatusCode);
        Assert.Equal("true", context.Response.Headers[ResultHttpExtensions.TransientHeaderName].ToString());
        context.Response.Body.Position = 0;
        using var problem = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.True(problem.RootElement.GetProperty("transient").GetBoolean());
    }

    /// <summary>Unauthorized failures remain bodyless but advertise their authentication scheme.</summary>
    [Fact]
    public async Task ShouldChallengeBearerWithoutLeakingUnauthorizedDetail()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var result = Result.Failure(new RequestError(RequestErrorKind.Unauthorized, "IDX secret", false));

        await result.ToHttpResult().ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.Equal("Bearer", context.Response.Headers.WWWAuthenticate.ToString());
        Assert.Equal("false", context.Response.Headers[ResultHttpExtensions.TransientHeaderName].ToString());
        Assert.Equal(0, context.Response.Body.Length);
    }

    /// <summary>
    ///     Verifies that a caller who does carry the required permission claim reaches the handler.
    /// </summary>
    [Fact]
    public async Task ShouldSucceedWhenCallerHasRequiredPermission()
    {
        var client = await StartAsync(app => app.MapPortiaPost<HttpGuardedAction>("/guarded"));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/guarded")
        {
            Content = JsonContent.Create(new { })
        };
        request.Headers.Add("X-Debug-Permission", "http:guarded");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    /// <summary>
    ///     Closes the last named, previously-unverified gap: a nested body property whose type is a
    ///     value type (a <c>record struct</c>, no <c>TryParse</c>) rather than a reference type.
    ///     <c>JsonElement.Deserialize&lt;TValue&gt;()</c> returns <c>TValue?</c>, i.e.
    ///     <see cref="Nullable{T}" /> for a value type — this only ever verified working for
    ///     reference-type nested shapes (<see cref="HttpOrderLine" />'s <see cref="List{T}" />)
    ///     before now.
    /// </summary>
    [Fact]
    public async Task ShouldBindValueTypeNestedBodyProperty()
    {
        var client = await StartAsync(app => app.MapPortiaPost<HttpCreatePayment, Uuid>("/payments"));

        var response =
            await client.PostAsJsonAsync("/payments", new { amount = new { currency = "USD", cents = 500 } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var paymentId = await response.Content.ReadFromJsonAsync<Uuid>();
        Assert.NotEqual(Uuid.Empty, paymentId);
    }

    /// <summary>
    ///     Verifies streaming as an incrementally-flushed JSON array.
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
    ///     Verifies streaming as Server-Sent Events, driven by the exact same
    ///     <see cref="IStreamRequestHandler{TRequest,TOut}" /> as the JSON-array mapping above.
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

    async Task<HttpClient> StartAsync(Action<IEndpointRouteBuilder> map,
        Action<IServiceCollection>? configureServices = null)
    {
        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        _ = builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);
        _ = builder.Services.AddSingleton<IPermissionEvaluator, DebugHeaderPermissionEvaluator>();
        configureServices?.Invoke(builder.Services);
        _ = builder.Services.AddFrameworkTests();

        _app = builder.Build();

        _ = _app.Use(async (context, next) =>
        {
            if (context.Request.Headers.TryGetValue("X-Debug-Permission", out var permission) && permission.Count > 0)
            {
                var identity = new ClaimsIdentity([new Claim("permission", permission[0]!)], "Debug");
                context.User = new ClaimsPrincipal(identity);
            }

            await next(context);
        });

        map(_app);

        await _app.StartAsync();
        return _app.GetTestClient();
    }

    sealed class DebugHeaderPermissionEvaluator : IPermissionEvaluator
    {
        public ValueTask<Result> EvaluateAsync(ClaimsPrincipal actor, string permission,
            CancellationToken ct = default) =>
            ValueTask.FromResult(actor.HasClaim("permission", permission)
                ? Result.Success
                : Result.Failure(new RequestError(RequestErrorKind.Forbidden, $"Missing permission '{permission}'.")));
    }

    sealed class RecordingRequestQueuePublisher : IRequestQueuePublisher
    {
        public List<object> Enqueued { get; } = [];
        public List<string?> ActorTokens { get; } = [];

        public ValueTask EnqueueAsync<TRequest>(TRequest request, RequestRouteValues routeValues, string? actorToken,
            RequestMetadata metadata, CancellationToken ct = default)
            where TRequest : IRequest, IQueuable
        {
            Enqueued.Add(request);
            ActorTokens.Add(actorToken);
            return ValueTask.CompletedTask;
        }
    }
}
