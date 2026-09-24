using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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

    /// <inheritdoc />
    [Fact]
    public async Task ShouldRunCustomBindingPipelineAndResultHook()
    {
        var client = await StartAsync(app => app.MapPortiaGet<HttpGetWidget, string>("/custom/{widget_id}", endpoint =>
            endpoint
                .Parameter<Uuid>("widget_id", PortiaHttpParameterLocation.Route)
                .Parameter<bool>("archived", PortiaHttpParameterLocation.Header, false)
                .OnBind(http => new HttpGetWidget(
                    Uuid.Parse((string)http.Request.RouteValues["widget_id"]!, CultureInfo.InvariantCulture),
                    bool.TryParse(http.Request.Headers["archived"], out var archived) && archived))
                .Produces(StatusCodes.Status302Found)
                .OnResult((_, result) => result.IsSuccess
                    ? Results.Redirect("/done?value=" + Uri.EscapeDataString(result.Value))
                    : null)));
        var widgetId = Uuid.CreateVersion4();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/custom/{widgetId}");
        request.Headers.Add("archived", "true");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal($"/done?value={Uri.EscapeDataString($"{widgetId} (archived: True)")}",
            response.Headers.Location?.OriginalString);
    }

    /// <summary>
    ///     Verifies the escape hatch for a request shape the default binder rejects: a complex filter
    ///     object on a GET is assembled by <c>OnBind</c> and still dispatched through the bus.
    /// </summary>
    [Fact]
    public async Task ShouldBindShapeUnsupportedByDefaultBinderThroughOnBind()
    {
        var client = await StartAsync(app => app.MapPortiaGet<HttpSearchWidgets, string>("/search", endpoint => endpoint
            .Parameter<string>("name", PortiaHttpParameterLocation.Query)
            .Parameter<int>("limit", PortiaHttpParameterLocation.Query)
            .OnBind(http => new HttpSearchWidgets(new HttpWidgetFilter(
                http.Request.Query["name"].ToString(),
                int.Parse(http.Request.Query["limit"].ToString(), CultureInfo.InvariantCulture))))));

        var response = await client.GetAsync("/search?name=gear&limit=3");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("gear:3", await response.Content.ReadFromJsonAsync<string>());
    }

    /// <summary>
    ///     Verifies a sign-in style flow: the default binder reads the form post, and after the
    ///     business operation succeeds the result hook sets a cookie and a header and redirects.
    /// </summary>
    [Fact]
    public async Task ShouldBindFormPostByDefaultThenSetCookieHeaderAndRedirect()
    {
        var client = await StartAsync(app => app.MapPortiaPost<HttpOptionalBody, string>("/sign-in", endpoint =>
            endpoint
                .Produces(StatusCodes.Status302Found)
                .OnResult((http, result) =>
                {
                    if (!result.IsSuccess)
                        return null;
                    http.Response.Cookies.Append("session", result.Value, new CookieOptions { HttpOnly = true });
                    http.Response.Headers["X-Signed-In"] = result.Value;
                    return Results.Redirect("/home");
                })).DisableAntiforgery());

        var response = await client.PostAsync("/sign-in", Form(("value", "jeff")));

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("/home", response.Headers.Location?.OriginalString);
        Assert.Equal("jeff", response.Headers.GetValues("X-Signed-In").Single());
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith("session=jeff", cookie, StringComparison.Ordinal);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Verifies default binding cascades route, body, then query for each member: the body may be
    ///     a URL-encoded form, a multipart form, or JSON, with the same wire names, scalar parsing,
    ///     defaults, blank-field and checkbox handling, and a member the body omits comes from the query.
    /// </summary>
    [Fact]
    public async Task ShouldBindRouteAndBodyFromFormOrJson()
    {
        var client = await StartAsync(app => app.MapPortiaPost<HttpUpdateWidget, string>("/widgets/{widget_id}")
            .DisableAntiforgery());
        var widgetId = Uuid.CreateVersion4();

        var urlEncoded = await client.PostAsync($"/widgets/{widgetId}?dry_run=true",
            Form(("name", "gear"), ("quantity", "3"), ("active", "on"), ("priority", "")));
        using var multipartContent = new MultipartFormDataContent
        {
            { new StringContent("cog"), "name" },
            { new StringContent("4"), "quantity" },
            { new StringContent("7"), "priority" }
        };
        var multipart = await client.PostAsync($"/widgets/{widgetId}", multipartContent);
        var json = await client.PostAsJsonAsync($"/widgets/{widgetId}?dry_run=true",
            new { name = "bolt", quantity = 5, active = true });
        var bodyBeforeQuery = await client.PostAsJsonAsync($"/widgets/{widgetId}?dry_run=true&quantity=9",
            new { name = "nut", quantity = 6, active = false, dry_run = false });
        var formBeforeQuery = await client.PostAsync($"/widgets/{widgetId}?name=query",
            Form(("name", "form"), ("quantity", "2")));
        var queryOnly = await client.PostAsync($"/widgets/{widgetId}?name=washer&quantity=8&active=true", null);
        // ASP.NET's checkbox helpers post the checkbox value followed by a hidden "false".
        var helperChecked = await client.PostAsync($"/widgets/{widgetId}",
            Form(("name", "pin"), ("quantity", "1"), ("active", "true"), ("active", "false")));
        var helperUnchecked = await client.PostAsync($"/widgets/{widgetId}",
            Form(("name", "pin"), ("quantity", "1"), ("active", "false")));
        var jsonMissingActive =
            await client.PostAsJsonAsync($"/widgets/{widgetId}", new { name = "nut", quantity = 6 });

        Assert.Equal($"{widgetId} gear 3 True none dry-run", await ReadSuccess(urlEncoded));
        Assert.Equal($"{widgetId} cog 4 False 7", await ReadSuccess(multipart));
        Assert.Equal($"{widgetId} bolt 5 True none dry-run", await ReadSuccess(json));
        Assert.Equal($"{widgetId} nut 6 False none", await ReadSuccess(bodyBeforeQuery));
        Assert.Equal($"{widgetId} form 2 False none", await ReadSuccess(formBeforeQuery));
        Assert.Equal($"{widgetId} washer 8 True none", await ReadSuccess(queryOnly));
        Assert.Equal($"{widgetId} pin 1 True none", await ReadSuccess(helperChecked));
        Assert.Equal($"{widgetId} pin 1 False none", await ReadSuccess(helperUnchecked));
        Assert.Equal(HttpStatusCode.BadRequest, jsonMissingActive.StatusCode);
    }

    /// <summary>
    ///     Verifies <c>FromQuery</c> binds a request member only from the query string, for both JSON
    ///     and form bodies, while the remaining members keep the route, body, query cascade.
    /// </summary>
    [Fact]
    public async Task ShouldBindFromQueryMemberOnlyFromQueryString()
    {
        var client = await StartAsync(app => app.MapPortiaPost<HttpUpdateWidget, string>("/widgets/{widget_id}",
            endpoint => endpoint
                .FromQuery(x => x.DryRun)
                .FromQuery(x => x.Priority)).DisableAntiforgery());
        var widgetId = Uuid.CreateVersion4();

        var jsonQuery = await client.PostAsJsonAsync($"/widgets/{widgetId}?dry_run=true&priority=2",
            new { name = "gear", quantity = 1, active = true, dry_run = false, priority = 9 });
        var jsonBodyIgnored = await client.PostAsJsonAsync($"/widgets/{widgetId}",
            new { name = "gear", quantity = 1, active = true, dry_run = true, priority = 9 });
        var formQuery = await client.PostAsync($"/widgets/{widgetId}?dry_run=true",
            Form(("name", "cog"), ("quantity", "2"), ("dry_run", "false"), ("priority", "9")));
        var invalidQuery = await client.PostAsJsonAsync($"/widgets/{widgetId}?dry_run=maybe",
            new { name = "gear", quantity = 1, active = true });

        Assert.Equal($"{widgetId} gear 1 True 2 dry-run", await ReadSuccess(jsonQuery));
        Assert.Equal($"{widgetId} gear 1 True none", await ReadSuccess(jsonBodyIgnored));
        Assert.Equal($"{widgetId} cog 2 False none dry-run", await ReadSuccess(formQuery));
        Assert.Equal(HttpStatusCode.BadRequest, invalidQuery.StatusCode);
    }

    /// <summary>Verifies a required <c>FromQuery</c> member is not satisfied by the body.</summary>
    [Fact]
    public async Task ShouldRequireFromQueryMemberInQueryString()
    {
        var client = await StartAsync(app => app.MapPortiaPost<HttpUpdateWidget, string>("/required-query/{widget_id}",
            endpoint => endpoint
                .FromQuery(x => x.Quantity)));
        var widgetId = Uuid.CreateVersion4();

        var missing = await client.PostAsJsonAsync($"/required-query/{widgetId}",
            new { name = "gear", quantity = 1, active = true });
        var supplied = await client.PostAsJsonAsync($"/required-query/{widgetId}?quantity=4",
            new { name = "gear", active = true });

        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal($"{widgetId} gear 4 True none", await ReadSuccess(supplied));
    }

    /// <summary>Verifies misdeclared <c>FromQuery</c> members fail when the route is mapped.</summary>
    [Fact]
    public void ShouldRejectInvalidFromQueryDeclarationsWhenMapping()
    {
        var builder = WebApplication.CreateBuilder();
        _ = builder.Services.AddFrameworkTests();
        _app = builder.Build();

        var route = Assert.Throws<InvalidOperationException>(() => _app.MapPortiaPost<HttpUpdateWidget, string>(
            "/route/{widget_id}", endpoint => endpoint.FromQuery(x => x.WidgetId)));
        var complex = Assert.Throws<InvalidOperationException>(() => _app.MapPortiaPost<HttpCreateOrder, Uuid>(
            "/complex", endpoint => endpoint.FromQuery(x => x.Lines)));
        var custom = Assert.Throws<InvalidOperationException>(() => _app.MapPortiaPost<HttpOptionalBody, string>(
            "/custom", endpoint => endpoint.FromQuery(x => x.Value).NoInput().OnBind(_ => new HttpOptionalBody())));
        var nested = Assert.Throws<ArgumentException>(() => new PortiaEndpointConfiguration<HttpGetWidget, string>()
            .FromQuery(x => x.WidgetId.ToString()));
        var duplicate = Assert.Throws<InvalidOperationException>(() =>
            new PortiaEndpointConfiguration<HttpGetWidget, string>()
                .FromQuery(x => x.IncludeArchived).FromQuery(x => x.IncludeArchived));

        Assert.Contains("already bound from the route", route.Message, StringComparison.Ordinal);
        Assert.Contains("must be a string, enum, or TryParse scalar", complex.Message, StringComparison.Ordinal);
        Assert.Contains("OnBind", custom.Message, StringComparison.Ordinal);
        Assert.Contains("direct request member", nested.Message, StringComparison.Ordinal);
        Assert.Contains("more than once", duplicate.Message, StringComparison.Ordinal);
    }

    /// <summary>Verifies form fields get the same validation as query values.</summary>
    [Theory]
    [InlineData("name=gear")]
    [InlineData("name=gear&quantity=many")]
    [InlineData("name=gear&quantity=")]
    [InlineData("name=gear&quantity=1&quantity=2")]
    [InlineData("name=gear&quantity=1&active=maybe")]
    [InlineData("name=gear&quantity=1&active=true&active=maybe")]
    public async Task ShouldRejectInvalidFormFields(string form)
    {
        var client = await StartAsync(app => app.MapPortiaPost<HttpUpdateWidget, string>("/widgets/{widget_id}")
            .DisableAntiforgery());

        var response = await client.PostAsync($"/widgets/{Uuid.CreateVersion4()}",
            new StringContent(form, Encoding.UTF8, "application/x-www-form-urlencoded"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>Verifies a body with complex members stays JSON-only, refusing a form as an unsupported media type.</summary>
    [Fact]
    public async Task ShouldRejectFormPostForComplexBody()
    {
        var client = await StartAsync(app => app.MapPortiaPost<HttpCreateOrder, Uuid>("/orders"));

        var response = await client.PostAsync("/orders", Form(("lines", "x")));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    /// <summary>
    ///     Verifies a registered antiforgery service guards default form binding: a missing token is
    ///     rejected, a valid token binds, JSON is unaffected, and an endpoint can opt out.
    /// </summary>
    [Fact]
    public async Task ShouldValidateAntiforgeryForFormPostsWhenRegistered()
    {
        var client = await StartAsync(app =>
        {
            _ = app.MapGet("/token", (HttpContext http, IAntiforgery antiforgery) =>
            {
                var tokens = antiforgery.GetAndStoreTokens(http);
                return Results.Text($"{tokens.FormFieldName}={tokens.RequestToken}");
            });
            _ = app.MapPortiaPost<HttpOptionalBody, string>("/guarded-form");
            _ = app.MapPortiaPost<HttpUpdateWidget, string>("/open-form/{widget_id}").DisableAntiforgery();
        }, services => services.AddAntiforgery());

        var missing = await client.PostAsync("/guarded-form", Form(("value", "x")));
        var json = await client.PostAsJsonAsync("/guarded-form", new { value = "json" });
        var widgetId = Uuid.CreateVersion4();
        var optedOut = await client.PostAsync($"/open-form/{widgetId}", Form(("name", "open"), ("quantity", "1")));

        var tokenResponse = await client.GetAsync("/token");
        var field = (await tokenResponse.Content.ReadAsStringAsync()).Split('=', 2);
        using var valid = new HttpRequestMessage(HttpMethod.Post, "/guarded-form")
        {
            Content = Form(("value", "valid"), (field[0], field[1]))
        };
        valid.Headers.Add("Cookie",
            tokenResponse.Headers.GetValues("Set-Cookie").Select(cookie => cookie.Split(';')[0]));
        var accepted = await client.SendAsync(valid);

        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal("json", await ReadSuccess(json));
        Assert.Equal($"{widgetId} open 1 False none", await ReadSuccess(optedOut));
        Assert.Equal("valid", await ReadSuccess(accepted));
    }

    /// <summary>
    ///     Verifies default form binding fails closed without an antiforgery service: a cross-site
    ///     forgeable form post is rejected as an unsupported media type, JSON still binds, and an
    ///     endpoint that calls <c>DisableAntiforgery()</c> accepts forms explicitly.
    /// </summary>
    [Fact]
    public async Task ShouldRejectFormPostsWhenAntiforgeryIsNotRegistered()
    {
        var client = await StartAsync(app =>
        {
            _ = app.MapPortiaPost<HttpOptionalBody, string>("/unguarded-form");
            _ = app.MapPortiaPost<HttpUpdateWidget, string>("/open-form/{widget_id}").DisableAntiforgery();
        });
        var widgetId = Uuid.CreateVersion4();

        var urlEncoded = await client.PostAsync("/unguarded-form", Form(("value", "x")));
        using var multipartContent = new MultipartFormDataContent { { new StringContent("x"), "value" } };
        var multipart = await client.PostAsync("/unguarded-form", multipartContent);
        var json = await client.PostAsJsonAsync("/unguarded-form", new { value = "json" });
        var optedOut = await client.PostAsync($"/open-form/{widgetId}", Form(("name", "open"), ("quantity", "1")));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, urlEncoded.StatusCode);
        Assert.Equal("application/problem+json", urlEncoded.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, multipart.StatusCode);
        Assert.Equal("json", await ReadSuccess(json));
        Assert.Equal($"{widgetId} open 1 False none", await ReadSuccess(optedOut));
    }

    /// <summary>
    ///     Verifies default JSON binding refuses every body a cross-site page can send without a CORS
    ///     preflight, even where antiforgery guards forms: a <c>text/plain</c> body holding valid JSON,
    ///     an empty <c>text/plain</c> body on an optional request, and an untyped JSON body of known or
    ///     unknown length are 415,
    ///     while declared JSON media types and bodiless posts still bind.
    /// </summary>
    [Fact]
    public async Task ShouldRejectBodiesBrowsersSendCrossSiteWithoutPreflight()
    {
        var client = await StartAsync(app =>
        {
            _ = app.MapPortiaPost<HttpOptionalBody, string>("/optional");
            _ = app.MapPortiaPost<HttpUpdateWidget, string>("/widgets/{widget_id}");
        }, services => services.AddAntiforgery());
        var widgetId = Uuid.CreateVersion4();

        var plainJson = await client.PostAsync($"/widgets/{widgetId}", new StringContent(
            /*lang=json,strict*/ """{"name":"gear","quantity":1,"active":true}""", Encoding.UTF8, "text/plain"));
        var plainEmpty = await client.PostAsync("/optional", new StringContent("", Encoding.UTF8, "text/plain"));
        var untypedJson = await client.PostAsync("/optional",
            new ByteArrayContent( /*lang=json,strict*/ """{"value":"untyped"}"""u8.ToArray()));
        using var untypedChunkedContent =
            new UnknownLengthContent( /*lang=json,strict*/ """{"value":"chunked"}"""u8.ToArray());
        var untypedChunked = await client.PostAsync("/optional", untypedChunkedContent);
        var suffixJson = await client.PostAsync("/optional", new StringContent(
            /*lang=json,strict*/ """{"value":"suffix"}""", Encoding.UTF8, "application/merge-patch+json"));
        var bodiless = await client.PostAsync("/optional", null);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, plainJson.StatusCode);
        Assert.Equal("application/problem+json", plainJson.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, plainEmpty.StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, untypedJson.StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, untypedChunked.StatusCode);
        Assert.Equal("suffix", await ReadSuccess(suffixJson));
        Assert.Equal("fallback", await ReadSuccess(bodiless));
    }

    static FormUrlEncodedContent Form(params (string Name, string Value)[] fields) =>
        new(fields.Select(field => new KeyValuePair<string, string>(field.Name, field.Value)));

    static async Task<string?> ReadSuccess(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode,
            $"Expected success, received {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<string>();
    }

    /// <summary>
    ///     Verifies a result hook can add headers and cookies around the default binding and default
    ///     JSON response by returning <see langword="null" />.
    /// </summary>
    [Fact]
    public async Task ShouldKeepDefaultBindingAndResponseWhenResultHookOnlyAddsHeadersAndCookies()
    {
        var client = await StartAsync(app => app.MapPortiaGet<HttpGetWidget, string>("/decorated/{widget_id}",
            endpoint => endpoint
                .OnResult((http, result) =>
                {
                    http.Response.Headers.CacheControl = "no-store";
                    http.Response.Cookies.Append("last_widget", result.Value);
                    return null;
                })));
        var widgetId = Uuid.CreateVersion4();

        var response = await client.GetAsync($"/decorated/{widgetId}?include_archived=true");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal($"{widgetId} (archived: True)", await response.Content.ReadFromJsonAsync<string>());
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.StartsWith("last_widget=", Assert.Single(response.Headers.GetValues("Set-Cookie")),
            StringComparison.Ordinal);
    }

    /// <summary>
    ///     Verifies a custom-bound request still goes through authorization, and a no-result endpoint's
    ///     hook can redirect a failure while adjusting cookies around the default success response.
    /// </summary>
    [Fact]
    public async Task ShouldAuthorizeCustomBoundRequestAndLetResultHookHandleFailureAndSuccess()
    {
        var client = await StartAsync(app => app.MapPortiaPost<HttpGuardedAction>("/sign-out", endpoint => endpoint
            .NoInput()
            .OnBind(_ => new HttpGuardedAction())
            .OnResult((http, result) =>
            {
                if (!result.IsSuccess)
                    return result.Error.Kind == RequestErrorKind.Forbidden ? Results.Redirect("/login") : null;
                http.Response.Cookies.Delete("session");
                return null;
            })));

        var denied = await client.PostAsync("/sign-out", null);
        using var permitted = new HttpRequestMessage(HttpMethod.Post, "/sign-out");
        permitted.Headers.Add("X-Debug-Permission", "http:guarded");
        var allowed = await client.SendAsync(permitted);

        Assert.Equal(HttpStatusCode.Found, denied.StatusCode);
        Assert.Equal("/login", denied.Headers.Location?.OriginalString);
        Assert.False(denied.Headers.Contains("Set-Cookie"));
        Assert.True(allowed.IsSuccessStatusCode, $"Expected success, received {(int)allowed.StatusCode}");
        Assert.StartsWith("session=;", Assert.Single(allowed.Headers.GetValues("Set-Cookie")),
            StringComparison.Ordinal);
    }

    /// <inheritdoc />
    [Fact]
    public async Task ShouldValidateCustomRouteParametersAgainstCompleteGroupedPattern()
    {
        var client = await StartAsync(app => app.MapGroup("/tenants/{tenant_id}")
            .MapPortiaGet<HttpGetWidget, string>("/widgets/{widget_id}", endpoint => endpoint
                .Parameter<string>("tenant_id", PortiaHttpParameterLocation.Route)
                .Parameter<Uuid>("widget_id", PortiaHttpParameterLocation.Route)
                .OnBind(http => new HttpGetWidget(
                    Uuid.Parse((string)http.Request.RouteValues["widget_id"]!, CultureInfo.InvariantCulture)))));
        var widgetId = Uuid.CreateVersion4();

        var response = await client.GetAsync($"/tenants/acme/widgets/{widgetId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <inheritdoc />
    [Fact]
    public void ShouldRejectDuplicateCustomBodyContentTypes()
    {
        var configuration = new PortiaEndpointConfiguration<HttpSendPing>();
        Assert.Throws<InvalidOperationException>(() => configuration.Accepts<string>(
            "application/json", additionalContentTypes: "APPLICATION/JSON"));
    }

    /// <inheritdoc />
    [Fact]
    public async Task ShouldApplyBodyLimitBeforeCustomBinderRuns()
    {
        var binderCalled = false;
        var client = await StartAsync(app => app.MapPortiaPost<HttpOptionalBody, string>("/custom-body", endpoint =>
                endpoint
                    .Accepts<string>("text/plain")
                    .OnBind(_ =>
                    {
                        binderCalled = true;
                        return new HttpOptionalBody();
                    })),
            services => services.Configure<PortiaHttpOptions>(options => options.MaxJsonBodyBytes = 1));

        var response = await client.PostAsync("/custom-body", new StringContent("too large"));

        Assert.False(binderCalled);
        Assert.True(response.StatusCode == HttpStatusCode.RequestEntityTooLarge,
            $"Expected 413, received {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    /// <summary>
    ///     Verifies a chunked form body over the limit is a 413 when the server enforces the body size
    ///     itself, as Kestrel does once Portia lowers <see cref="IHttpMaxRequestBodySizeFeature" />.
    /// </summary>
    [Fact]
    public async Task ShouldReturnPayloadTooLargeWhenServerRejectsChunkedFormBody()
    {
        var client = await StartAsync(app =>
            {
                UseServerEnforcedBodyLimit(app, null);
                _ = app.MapPortiaPost<HttpUpdateWidget, string>("/server-limited-form/{widget_id}")
                    .DisableAntiforgery();
            },
            services => services.Configure<PortiaHttpOptions>(options => options.MaxJsonBodyBytes = 24));
        var widgetId = Uuid.CreateVersion4();
        using var small = new UnknownLengthContent("name=gear&quantity=1"u8.ToArray());
        small.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
        using var large = new UnknownLengthContent("name=gear&quantity=1&active=true"u8.ToArray());
        large.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");

        using var accepted = await client.PostAsync($"/server-limited-form/{widgetId}", small);
        using var rejected = await client.PostAsync($"/server-limited-form/{widgetId}", large);

        Assert.Equal($"{widgetId} gear 1 False none", await ReadSuccess(accepted));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, rejected.StatusCode);
    }

    /// <summary>An antiforgery form read must preserve an underlying unknown-length body-limit failure as 413.</summary>
    [Fact]
    public async Task ShouldPreservePayloadTooLargeWhenAntiforgeryReadsChunkedFormBody()
    {
        var client = await StartAsync(app =>
            {
                UseServerEnforcedBodyLimit(app, null);
                _ = app.MapPortiaPost<HttpUpdateWidget, string>("/antiforgery-limited-form/{widget_id}");
            },
            services =>
            {
                _ = services.AddAntiforgery();
                _ = services.Configure<PortiaHttpOptions>(options => options.MaxJsonBodyBytes = 24);
            });
        using var large = new UnknownLengthContent("name=gear&quantity=1&active=true"u8.ToArray());
        large.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");

        using var response = await client.PostAsync(
            $"/antiforgery-limited-form/{Uuid.CreateVersion4()}", large);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    /// <summary>Verifies a JSON body rejected by the server's own body size limit is a 413.</summary>
    [Fact]
    public async Task ShouldReturnPayloadTooLargeWhenServerRejectsJsonBody()
    {
        var client = await StartAsync(app =>
        {
            UseServerEnforcedBodyLimit(app, 8);
            _ = app.MapPortiaPost<HttpUpdateWidget, string>("/server-limited-json/{widget_id}");
        });
        using var content = new UnknownLengthContent("""{"name":"gear","quantity":1,"active":true}"""u8.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var response = await client.PostAsync($"/server-limited-json/{Uuid.CreateVersion4()}", content);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    // Emulates Kestrel: the transport itself throws a 413 BadHttpRequestException once the received
    // body exceeds IHttpMaxRequestBodySizeFeature, before the excess bytes reach any wrapping stream.
    static void UseServerEnforcedBodyLimit(IEndpointRouteBuilder app, long? maximum) =>
        _ = ((IApplicationBuilder)app).Use(async (context, next) =>
        {
            var feature = new TestMaxRequestBodySizeFeature(maximum, false);
            context.Features.Set<IHttpMaxRequestBodySizeFeature>(feature);
            context.Request.Body = new ServerLimitedStream(context.Request.Body, feature);
            await next(context);
        });

    /// <inheritdoc />
    [Fact]
    public async Task ShouldApplyBodyLimitToChunkedCustomBinderReads()
    {
        var client = await StartAsync(app => app.MapPortiaPost<HttpOptionalBody, string>("/custom-chunked", endpoint =>
                endpoint
                    .Accepts<Stream>("application/octet-stream")
                    .OnBind(async (http, ct) =>
                    {
                        using var reader = new StreamReader(http.Request.Body);
                        _ = await reader.ReadToEndAsync(ct);
                        return new HttpOptionalBody();
                    })),
            services => services.Configure<PortiaHttpOptions>(options => options.MaxJsonBodyBytes = 8));

        using var response = await client.PostAsync("/custom-chunked", new UnknownLengthContent(new byte[9]));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    /// <summary>Verifies unread chunked data cannot bypass the declared custom-body limit.</summary>
    [Fact]
    public async Task ShouldRejectChunkedCustomBodyBeforePartiallyReadingBinderRuns()
    {
        var binderCalled = false;
        var resultCalled = false;
        var client = await StartAsync(app => app.MapPortiaPost<HttpOptionalBody, string>("/custom-chunked-prefix",
                endpoint => endpoint
                    .Accepts<Stream>("application/octet-stream")
                    .OnBind(async (http, ct) =>
                    {
                        binderCalled = true;
                        var prefix = new byte[1];
                        _ = await http.Request.Body.ReadAsync(prefix, ct);
                        return new HttpOptionalBody();
                    })
                    .OnResult((_, _) =>
                    {
                        resultCalled = true;
                        return null;
                    })),
            services => services.Configure<PortiaHttpOptions>(options => options.MaxJsonBodyBytes = 8));

        using var response = await client.PostAsync("/custom-chunked-prefix", new UnknownLengthContent(new byte[9]));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.False(binderCalled);
        Assert.False(resultCalled);
    }

    /// <inheritdoc />
    [Fact]
    public async Task ShouldAllowChunkedCustomBodyAtLimit()
    {
        var client = await StartAsync(app => app.MapPortiaPost<HttpOptionalBody, string>("/custom-chunked-limit",
                endpoint => endpoint
                    .Accepts<Stream>("application/octet-stream")
                    .OnBind(async (http, ct) =>
                    {
                        using var reader = new StreamReader(http.Request.Body);
                        _ = await reader.ReadToEndAsync(ct);
                        return new HttpOptionalBody();
                    })),
            services => services.Configure<PortiaHttpOptions>(options => options.MaxJsonBodyBytes = 8));

        using var response = await client.PostAsync("/custom-chunked-limit", new UnknownLengthContent(new byte[8]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>Portia tightens, but never relaxes, the web server's request-body limit.</summary>
    [Theory]
    [InlineData(null, false, 8L)]
    [InlineData(16L, false, 8L)]
    [InlineData(4L, false, 4L)]
    [InlineData(16L, true, 16L)]
    public void ShouldRespectStricterOrReadOnlyServerBodyLimit(long? serverMaximum, bool readOnly, long expected)
    {
        using var services = new ServiceCollection()
            .Configure<PortiaHttpOptions>(options => options.MaxJsonBodyBytes = 8)
            .BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        var feature = new TestMaxRequestBodySizeFeature(serverMaximum, readOnly);
        context.Features.Set<IHttpMaxRequestBodySizeFeature>(feature);

        PortiaHttpBinding.EnsureBodyWithinLimit(context);

        Assert.Equal(expected, feature.MaxRequestBodySize);
    }

    /// <inheritdoc />
    [Fact]
    public async Task ShouldMapCustomFormatFailureToBadRequest()
    {
        var client = await StartAsync(app => app.MapPortiaGet<HttpGetWidget, string>("/custom-format/{widget_id}",
            endpoint => endpoint
                .Parameter<Uuid>("widget_id", PortiaHttpParameterLocation.Route)
                .OnBind(http => new HttpGetWidget(
                    Uuid.Parse((string)http.Request.RouteValues["widget_id"]!, CultureInfo.InvariantCulture)))));

        var response = await client.GetAsync("/custom-format/not-a-uuid");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <inheritdoc />
    [Fact]
    public void ShouldFreezeConfigurationAfterMapping()
    {
        PortiaEndpointConfiguration<HttpOptionalBody, string>? captured = null;
        var builder = WebApplication.CreateBuilder();
        _ = builder.Services.AddFrameworkTests();
        _app = builder.Build();
        _ = _app.MapPortiaPost<HttpOptionalBody, string>("/frozen", endpoint =>
        {
            captured = endpoint;
            _ = endpoint.NoInput().OnBind(_ => new HttpOptionalBody());
        });

        var configuration = Assert.IsType<PortiaEndpointConfiguration<HttpOptionalBody, string>>(captured);
        foreach (var mutation in new Action[]
                 {
                     () => configuration.OnBind(_ => new HttpOptionalBody()),
                     () => configuration.OnResult((_, _) => null),
                     () => configuration.Accepts<string>("text/plain"),
                     () => configuration.Parameter<string>("value", PortiaHttpParameterLocation.Query),
                     () => configuration.NoInput(),
                     () => configuration.Produces(StatusCodes.Status201Created)
                 })
        {
            var error = Assert.Throws<InvalidOperationException>(mutation);
            Assert.Equal("Endpoint configuration cannot be changed after mapping.", error.Message);
        }
    }

    /// <inheritdoc />
    [Fact]
    public async Task ShouldRequireResultHookToHonorDeclaredSuccessResponse()
    {
        var configuration = new PortiaEndpointConfiguration<HttpOptionalBody, string>();
        _ = configuration.Produces(StatusCodes.Status302Found).OnResult((_, _) => null);
        configuration.Validate();

        var handler = Assert.IsType<Func<HttpContext, Result<string>, CancellationToken, ValueTask<IResult?>>>(
            configuration.ResultHandler);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await handler(new DefaultHttpContext(), Result<string>.Success("done"), default));
        var failureResponse = await handler(new DefaultHttpContext(), Result<string>.Failure(
            new RequestError(RequestErrorKind.NotFound, "missing")), default);

        Assert.Equal(
            "OnResult must return a response for a successful result when a custom success response is declared.",
            error.Message);
        Assert.Null(failureResponse);
    }

    /// <inheritdoc />
    [Fact]
    public async Task ShouldRejectInvalidGroupedCustomRouteAtStartup()
    {
        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        _ = builder.Services.AddFrameworkTests();
        var scheduler = new RecordingStartupScheduler();
        _ = builder.Services.AddSingleton<IRequestScheduler>(scheduler);
        _ = builder.Services.AddPortia()
            .AddRequestSchedule(new FitzHostedRequest(), new RequestScheduleSpec("0 0 * * *"),
                new RequestRouteValues("started"), RequestActor.System)
            .AddWorkers();
        _ = builder.Services.AddSingleton<IPermissionEvaluator>(TestPermissionEvaluator.AllowAll());
        _app = builder.Build();
        _ = _app.MapGroup("/tenants/{tenant_id}")
            .MapPortiaGet<HttpGetWidget, string>("/widgets/{widget_id}", endpoint => endpoint
                .Parameter<Uuid>("widget_id", PortiaHttpParameterLocation.Route)
                .OnBind(http => new HttpGetWidget(
                    Uuid.Parse((string)http.Request.RouteValues["widget_id"]!, CultureInfo.InvariantCulture))));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _app.StartAsync());

        Assert.Contains("Custom route parameters must exactly match", error.Message, StringComparison.Ordinal);
        Assert.Empty(scheduler.Requests);
    }

    /// <summary>Root route sources are validated before a Portia startup schedule can run.</summary>
    [Fact]
    public async Task ShouldRejectInvalidRootCustomRouteBeforeStartupSchedule()
    {
        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        _ = builder.Services.AddFrameworkTests();
        var scheduler = new RecordingStartupScheduler();
        _ = builder.Services.AddSingleton<IRequestScheduler>(scheduler);
        _ = builder.Services.AddPortia()
            .AddRequestSchedule(new FitzHostedRequest(), new RequestScheduleSpec("0 0 * * *"),
                new RequestRouteValues("started"), RequestActor.System)
            .AddWorkers();
        _ = builder.Services.AddSingleton<IPermissionEvaluator>(TestPermissionEvaluator.AllowAll());
        _app = builder.Build();
        _ = _app.MapPortiaGet<HttpGetWidget, string>(
            "/tenants/{tenant_id}/widgets/{widget_id}", endpoint => endpoint
                .Parameter<Uuid>("widget_id", PortiaHttpParameterLocation.Route)
                .OnBind(http => new HttpGetWidget(
                    Uuid.Parse((string)http.Request.RouteValues["widget_id"]!, CultureInfo.InvariantCulture))));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _app.StartAsync());

        Assert.Contains("Custom route parameters must exactly match", error.Message, StringComparison.Ordinal);
        Assert.Empty(scheduler.Requests);
    }

    /// <summary>A valid grouped route releases deferred startup schedules after its complete pattern is validated.</summary>
    [Fact]
    public async Task ShouldRunStartupScheduleAfterValidGroupedRouteValidation()
    {
        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        _ = builder.Services.AddFrameworkTests();
        var scheduler = new RecordingStartupScheduler();
        _ = builder.Services.AddSingleton<IRequestScheduler>(scheduler);
        _ = builder.Services.AddPortia()
            .AddRequestSchedule(new FitzHostedRequest(), new RequestScheduleSpec("0 0 * * *"),
                new RequestRouteValues("started"), RequestActor.System)
            .AddWorkers();
        _ = builder.Services.AddSingleton<IPermissionEvaluator>(TestPermissionEvaluator.AllowAll());
        _app = builder.Build();
        _ = _app.MapGroup("/tenants/{tenant_id}")
            .MapPortiaGet<HttpGetWidget, string>("/widgets/{widget_id}", endpoint => endpoint
                .Parameter<string>("tenant_id", PortiaHttpParameterLocation.Route)
                .Parameter<Uuid>("widget_id", PortiaHttpParameterLocation.Route)
                .OnBind(http => new HttpGetWidget(
                    Uuid.Parse((string)http.Request.RouteValues["widget_id"]!, CultureInfo.InvariantCulture))));

        await _app.StartAsync();

        Assert.Equal(["started"], scheduler.Requests);
    }

    /// <inheritdoc />
    [Fact]
    public async Task ShouldMapCustomJsonBindingFailureToBadRequest()
    {
        var client = await StartAsync(app => app.MapPortiaPost<HttpOptionalBody, string>("/custom-json", endpoint =>
            endpoint
                .Accepts<string>("application/json")
                .OnBind(_ => throw new JsonException("truncated"))));

        var response = await client.PostAsync("/custom-json",
            new StringContent("{", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
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
        request.Headers.Add("Authorization", "Bearer carried");

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

    /// <summary>
    ///     An anonymous caller has no credential a worker could validate again, so its preference is
    ///     declined and the request runs now rather than being accepted and then rejected unseen.
    /// </summary>
    [Fact]
    public async Task ShouldRunAnonymousCallerSynchronouslyWhenRespondAsyncIsPreferred()
    {
        var publisher = new RecordingRequestQueuePublisher();
        var client = await StartAsync(app => app.MapPortiaPost<HttpSendPing>("/ping"),
            services => services.AddSingleton<IRequestQueuePublisher>(publisher));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/ping") { Content = JsonContent.Create(new { }) };
        request.Headers.Add("Prefer", "respond-async");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(response.Headers.Contains("Preference-Applied"));
        Assert.Empty(publisher.Enqueued);
    }

    /// <summary>
    ///     Verifies a result hook keeps a queuable endpoint synchronous: <c>Prefer: respond-async</c>
    ///     is not honored, so the hook runs after the operation instead of a 202 bypassing it.
    /// </summary>
    [Fact]
    public async Task ShouldRunResultHookInsteadOfQueueingWhenRespondAsyncIsPreferred()
    {
        var publisher = new RecordingRequestQueuePublisher();
        var client = await StartAsync(app => app.MapPortiaPost<HttpSendPing>("/queued-sign-in", endpoint => endpoint
                .Produces(StatusCodes.Status302Found)
                .OnResult((_, result) => result.IsSuccess ? Results.Redirect("/home") : null)),
            services => services.AddSingleton<IRequestQueuePublisher>(publisher));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/queued-sign-in")
        { Content = JsonContent.Create(new { }) };
        request.Headers.Add("Prefer", "respond-async");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("/home", response.Headers.Location?.OriginalString);
        Assert.False(response.Headers.Contains("Preference-Applied"));
        Assert.Empty(publisher.Enqueued);
    }

    /// <summary>Verifies configuring a queuable endpoint without a result hook still honors respond-async.</summary>
    [Fact]
    public async Task ShouldStillQueueConfiguredEndpointWithoutResultHookWhenRespondAsyncIsPreferred()
    {
        var publisher = new RecordingRequestQueuePublisher();
        var client = await StartAsync(app => app.MapPortiaPost<HttpSendPing>("/configured-ping", endpoint => endpoint
                .Produces(StatusCodes.Status409Conflict)),
            services => services.AddSingleton<IRequestQueuePublisher>(publisher));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/configured-ping")
        { Content = JsonContent.Create(new { }) };
        request.Headers.Add("Prefer", "respond-async");
        request.Headers.Add("Authorization", "Bearer carried");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        _ = Assert.Single(publisher.Enqueued);
    }

    /// <summary>Verifies a result hook sees an authorization failure even when respond-async is preferred.</summary>
    [Fact]
    public async Task ShouldRunResultHookForForbiddenQueueableRequestWhenRespondAsyncIsPreferred()
    {
        var publisher = new RecordingRequestQueuePublisher();
        var client = await StartAsync(app => app.MapPortiaPost<HttpGuardedQueueAction>("/queued-guarded", endpoint =>
                endpoint
                    .OnResult((_, result) => result.IsSuccess ? null : Results.Redirect("/login"))),
            services => services.AddSingleton<IRequestQueuePublisher>(publisher));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/queued-guarded")
        { Content = JsonContent.Create(new { }) };
        request.Headers.Add("Prefer", "respond-async");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("/login", response.Headers.Location?.OriginalString);
        Assert.Empty(publisher.Enqueued);
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
    ///     Verifies that a client abort is requested cancellation, not a fault: the endpoint aborts the
    ///     connection rather than logging a fault and writing a 500 that nobody can receive.
    /// </summary>
    [Fact]
    public async Task ShouldAbortWithoutFaultWhenClientAbortsUnaryRequest()
    {
        var logs = new RecordingLoggerProvider();
        _ = await StartAsync(app => app.MapPortiaGet<HttpGetWidget, string>("/widgets/{widget_id}"),
            services => services.AddSingleton<ILoggerProvider>(logs));
        var endpoint = Assert.Single(((IEndpointRouteBuilder)_app!).DataSources
            .SelectMany(source => source.Endpoints).OfType<RouteEndpoint>());
        await using var scope = _app!.Services.CreateAsyncScope();
        var lifetime = new AbortedRequestLifetime();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
        context.Request.Method = HttpMethods.Get;
        context.Request.RouteValues["widget_id"] = Uuid.CreateVersion4().ToString();

        await endpoint.RequestDelegate!(context);

        Assert.True(lifetime.Aborted);
        Assert.NotEqual(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.DoesNotContain(logs.Levels, level => level >= LogLevel.Error);
    }

    /// <summary>
    ///     Verifies that a body property whose casing differs from the configured naming policy still
    ///     binds. The generated binder looks for the exact name first and falls back to a case-insensitive
    ///     match, which is what lets a hand-written client that sends <c>Value</c> work against an
    ///     endpoint whose policy produced <c>value</c>.
    /// </summary>
    /// <param name="json">The request body, spelling the property some other way.</param>
    [Theory]
    [InlineData( /*lang=json,strict*/ """{"Value":"bound"}""")]
    [InlineData( /*lang=json,strict*/ """{"VALUE":"bound"}""")]
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
        var result = Result.Failure(new RequestError(RequestErrorKind.Unauthorized, "IDX secret"));

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

    /// <summary>A failed stream guard is a problem response, because no JSON array was written yet.</summary>
    [Fact]
    public async Task ShouldReturnGuardProblemBeforeJsonStreamWhenStreamGuardFails()
    {
        var client = await StartAsync(app => app.MapPortiaGetStream<HttpListWidgets, string>("/guarded-widgets"),
            AddConflictStreamGuard);

        await AssertGuardConflictAsync(await client.GetAsync("/guarded-widgets"));
    }

    /// <summary>A failed stream guard is a problem response, because no event was written yet.</summary>
    [Fact]
    public async Task ShouldReturnGuardProblemBeforeServerSentEventsWhenStreamGuardFails()
    {
        var client = await StartAsync(app => app.MapPortiaGetSse<HttpListWidgets, string>("/guarded-widget-events"),
            AddConflictStreamGuard);

        await AssertGuardConflictAsync(await client.GetAsync("/guarded-widget-events"));
    }

    static void AddConflictStreamGuard(IServiceCollection services) => _ = services
        .AddScoped<ConflictStreamGuard>()
        .AddSingleton<RequestGuardRegistration>(new RequestGuardRegistration<HttpListWidgets, ConflictStreamGuard>());

    static async Task AssertGuardConflictAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("widgets are being rebuilt", problem.RootElement.GetProperty("detail").GetString());
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

    /// <summary>
    ///     Verifies that the <c>Prefer: respond-async</c> pivot authorizes before it accepts. The
    ///     queue write is durable and the 202 is final, so a caller who could not run the request
    ///     synchronously must not be able to place it on the queue either — otherwise an
    ///     unauthenticated caller can fill a durable queue with work that only fails much later,
    ///     at the worker, as a dead letter.
    /// </summary>
    [Fact]
    public async Task ShouldReturnForbiddenWithoutEnqueueingWhenCallerLacksPermissionAndPrefersRespondAsync()
    {
        var publisher = new RecordingRequestQueuePublisher();
        var client = await StartAsync(app => app.MapPortiaPost<HttpGuardedQueueAction>("/guarded-queue"),
            services => services.AddSingleton<IRequestQueuePublisher>(publisher));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/guarded-queue")
        {
            Content = JsonContent.Create(new { })
        };
        request.Headers.Add("Prefer", "respond-async");
        request.Headers.Add("Authorization", "Bearer carried");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(publisher.Enqueued);
        Assert.False(response.Headers.Contains("Preference-Applied"));
    }

    /// <summary>
    ///     A caller whose credential could not be carried to a worker is still refused for lacking
    ///     permission first, exactly as without the preference; the credential's shape is checked only
    ///     for a caller who could run the request.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ShouldReturnForbiddenBeforeCheckingTheCredentialWhenPreferringRespondAsync(bool cookieIdentity)
    {
        var publisher = new RecordingRequestQueuePublisher();
        var client = await StartAsync(app => app.MapPortiaPost<HttpGuardedQueueAction>("/guarded-queue"),
            services => services.AddSingleton<IRequestQueuePublisher>(publisher));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/guarded-queue")
        {
            Content = JsonContent.Create(new { })
        };
        request.Headers.Add("Prefer", "respond-async");
        if (cookieIdentity)
            request.Headers.Add("X-Debug-Permission", "http:other");
        else
            _ = request.Headers.TryAddWithoutValidation("Authorization", "Basic secret");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(publisher.Enqueued);
    }

    /// <summary>
    ///     Verifies the other half of that contract: an authorized caller still pivots to the queue,
    ///     so authorizing before accepting does not cost the asynchronous path.
    /// </summary>
    [Fact]
    public async Task ShouldRejectAuthenticatedAsyncCallerWithoutPortableBearerCredential()
    {
        var publisher = new RecordingRequestQueuePublisher();
        var client = await StartAsync(app => app.MapPortiaPost<HttpGuardedQueueAction>("/guarded-queue"),
            services => services.AddSingleton<IRequestQueuePublisher>(publisher));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/guarded-queue")
        {
            Content = JsonContent.Create(new { })
        };
        request.Headers.Add("Prefer", "respond-async");
        request.Headers.Add("X-Debug-Permission", "http:guarded-queue");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(publisher.Enqueued);
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

    sealed class AbortedRequestLifetime : IHttpRequestLifetimeFeature
    {
        public bool Aborted { get; private set; }

        public CancellationToken RequestAborted { get; set; } = new(true);

        public void Abort() => Aborted = true;
    }

    sealed class RecordingLoggerProvider : ILoggerProvider, ILogger
    {
        public ConcurrentQueue<LogLevel> Levels { get; } = new();

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Levels.Enqueue(logLevel);

        public void Dispose()
        {
        }
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

    internal sealed class ConflictStreamGuard : IRequestGuard<HttpListWidgets>
    {
        public ValueTask<Result> GuardAsync(IRequestContext<HttpListWidgets> context, CancellationToken ct) =>
            ValueTask.FromResult(
                Result.Failure(new RequestError(RequestErrorKind.Conflict, "widgets are being rebuilt")));
    }

    sealed class UnknownLengthContent(byte[] content) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(content).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    sealed class ServerLimitedStream(Stream inner, IHttpMaxRequestBodySizeFeature limit) : Stream
    {
        long _received;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _received;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => Receive(inner.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            Receive(await inner.ReadAsync(buffer, cancellationToken));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count,
            CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        int Receive(int read)
        {
            _received += read;
            return limit.MaxRequestBodySize is { } maximum && _received > maximum
                ? throw new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge)
                : read;
        }
    }

    sealed class TestMaxRequestBodySizeFeature(long? maximum, bool readOnly) : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly { get; } = readOnly;
        public long? MaxRequestBodySize { get; set; } = maximum;
    }
}
