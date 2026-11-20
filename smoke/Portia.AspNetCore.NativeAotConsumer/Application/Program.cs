using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text;
using Cntryl.Portia;
using Cntryl.Portia.Testing;

var generatedCatalog = new DomainEventTypeCatalog().AddPortiaGeneratedDomainEvents();
if (!generatedCatalog.TryResolve("smoke.catalog.changed", 1, out var catalogEvent)
    || catalogEvent != typeof(SmokeCatalogChanged))
    throw new InvalidOperationException("The packed generated event catalog did not resolve its declared event.");

var scenario = new ProjectorScenario().Given(new SmokeCatalogChanged(), new SmokeCatalogConfirmed());
var source = EventStreamPattern.ForPattern("smoke", "catalog");
var builder = WebApplication.CreateSlimBuilder(args);
builder.Services.AddSingleton(scenario.Store);
builder.WebHost.UseUrls("http://127.0.0.1:0");
_ = builder.Services.AddSingleton<SmokeGuardProbe>();
_ = builder.Services.AddSingleton<SmokeContinuationProbe>();
_ = builder.Services.AddPortia().ConfigureJson(options =>
    {
        options.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
        options.TypeInfoResolver = SmokeJsonContext.Default.WithAddedModifier(static info =>
        {
            if (info.Type == typeof(SmokeBinding))
                foreach (var property in info.Properties)
                    property.Name = "wire_" + property.Name;
        });
    })
    .AddExample("external", new App_A.FirstPayload("example"), use: PortiaContractUse.Output)
    .AddHttp(options =>
    {
        options.OpenApiSchemaType = type => type == typeof(SmokeNames)
            ? typeof(string[])
            : type == typeof(SmokeCount) ? typeof(int) : null;
    })
    .AddProjector<SmokeProjector>("SmokeProjector", source)
    .AddRequestHandler<SmokeRequestHandler>()
    .AddRequestHandler<SmokeExternalHandler>()
    .AddRequestHandler<SmokeUploadHandler>()
    .AddRequestHandler<SmokeBindingHandler>()
    .AddRequestHandler<SmokeQuoteHandler>()
    .AddRequestHandler<SmokeScheduleHandler>()
    .AddRequestHandler<SmokeRenameHandler>()
    .AddRequestHandler<SmokeArchiveHandler>()
    .AddRequestHandler<SmokeCreateHandler>()
    .AddRequestHandler<SmokeReplaceHandler>()
    .AddRequestPipelineBehavior<SmokeRequestBehavior>()
    .AddRequestGuard<SmokeRequestGuard>();
_ = builder.Services.AddCors(options => options.AddPolicy("smoke", policy => policy.WithOrigins("https://trusted.example")));
var app = builder.Build();
using (var scope = app.Services.CreateScope())
{
    var registration = scope.ServiceProvider.GetRequiredService<ProjectorRegistration>();
    var projector = registration.Resolve(scope.ServiceProvider);
    if (registration.ProjectorType != typeof(SmokeProjector) || projector.Name != "SmokeProjector")
        throw new InvalidOperationException("Native projector registration lost its application type or name.");
    await scenario.RunAsync(projector);
    if (scope.ServiceProvider.GetRequiredService<SmokeProjector>().Events != "OC")
        throw new InvalidOperationException("Native typed dispatch lost event order.");
}
_ = app.UseCors("smoke");
_ = app.MapPortiaOpenApi();
_ = app.MapPortiaGet<SmokeExternalRequest, App_A.FirstPayload>("/external-contract");
_ = app.MapPortiaGet<SmokeRequest, SmokePayload>("/smoke", endpoint => endpoint
    .Describe(x => x.Name, "Requested name.").DescribeResponse("Native response."));
_ = app.MapPortiaPost<SmokeBinding, string>("/smoke-binding", endpoint => endpoint.Describe(x => x.A, "First value.")
    .RequestExample("binding", new SmokeBinding(11, 22)).ResponseExample(200, "binding", "11,22"));
_ = app.MapGroup("/native").MapPortiaPost<SmokeCreate, PortiaCreateOutcome<SmokeResource>>("/resources",
    endpoint => endpoint.Created("/native/resources/{id}", includePayload: false));
_ = app.MapPortiaPut<SmokeReplace, PortiaCreateOutcome<SmokeResource>>("/native/resources/{resource}",
    endpoint => endpoint.Created("/native/resources/{id}", includePayload: false));
_ = app.MapPortiaGet<SmokeQuote, DecimalString>("/smoke-quotes/{amount}");
_ = app.MapPortiaPost<SmokeSchedule, string>("/smoke-schedule");
_ = app.MapPortiaPutByBody("/smoke-assets/{id}", "smokeAsset", "mode")
    .Case<SmokeRename>("rename").Case<SmokeArchive>("archive");
_ = app.MapPortiaPost<SmokeUpload, int>("/smoke-upload", endpoint => endpoint
    .Accepts<Stream>("application/octet-stream")
    .OnBind(async (http, ct) =>
    {
        await using var body = new MemoryStream();
        await http.Request.Body.CopyToAsync(body, ct);
        return new SmokeUpload(checked((int)body.Length));
    })
    .Produces<Stream>(StatusCodes.Status200OK, "application/octet-stream")
    .OnResult((_, result) => result.IsSuccess
        ? Results.Bytes(Encoding.UTF8.GetBytes(result.Value.ToString()), "application/octet-stream")
        : null));
// Referenced internal contexts are composed through public factory metadata, with reflection disabled.
if (JsonSerializer.IsReflectionEnabledByDefault)
    throw new InvalidOperationException("JSON reflection must be disabled in the NativeAOT consumer.");
var jsonOptions = app.Services.GetRequiredKeyedService<JsonSerializerOptions>(PortiaServiceKeys.Json);
var firstType = jsonOptions.GetTypeInfo<App_A.FirstPayload>();
var secondType = jsonOptions.GetTypeInfo<App.SecondPayload>();
if (JsonSerializer.Serialize(new App_A.FirstPayload("contracts"), firstType) != "{\"value\":\"contracts\"}"
    || JsonSerializer.Deserialize("{\"value\":42}", secondType)?.Value != 42)
    throw new InvalidOperationException("Referenced JSON context composition failed.");

await app.StartAsync();
using (var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) })
{
    foreach (var (repeated, expected) in new[] { (false, HttpStatusCode.Created), (true, HttpStatusCode.OK) })
    {
        using var createBody = new StringContent(repeated ? """{"repeated":true}""" : """{"repeated":false}""",
            Encoding.UTF8, "application/json");
        using var create = await client.PostAsync("/native/resources", createBody);
        if (create.StatusCode != expected || create.Headers.Location?.OriginalString != "/native/resources/1" ||
            (await create.Content.ReadAsStringAsync()).Length != 0)
            throw new InvalidOperationException($"Unexpected bodyless create response: {(int)create.StatusCode}.");
    }

    using var replaceBody = new StringContent("{}", Encoding.UTF8, "application/json");
    using var replace = await client.PutAsync("/native/resources/1", replaceBody);
    if (replace.StatusCode != HttpStatusCode.NoContent || replace.Headers.Location?.OriginalString != "/native/resources/1" ||
        (await replace.Content.ReadAsStringAsync()).Length != 0)
        throw new InvalidOperationException($"Unexpected bodyless replace response: {(int)replace.StatusCode}.");

    using var quote = await client.GetAsync("/smoke-quotes/1.0000000000000000000000000001");
    if (quote.StatusCode != HttpStatusCode.OK ||
        await quote.Content.ReadAsStringAsync() != "\"1.0000000000000000000000000001\"")
        throw new InvalidOperationException("Native decimal string lost precision.");
    using var rounded = await client.GetAsync("/smoke-quotes/1.00000000000000000000000000001");
    if (rounded.StatusCode != HttpStatusCode.BadRequest)
        throw new InvalidOperationException("Native decimal string accepted a rounded value.");

    using var missingSchedule = new StringContent("""{"plan":"scheduled"}""", Encoding.UTF8, "application/json");
    using var scheduleFailure = await client.PostAsync("/smoke-schedule", missingSchedule);
    if (scheduleFailure.StatusCode != HttpStatusCode.BadRequest)
        throw new InvalidOperationException("Native conditional validation accepted a missing value.");
    using var manual = new StringContent("""{"plan":"manual"}""", Encoding.UTF8, "application/json");
    using var scheduleSuccess = await client.PostAsync("/smoke-schedule", manual);
    if (scheduleSuccess.StatusCode != HttpStatusCode.OK)
        throw new InvalidOperationException("Native conditional validation rejected an inactive condition.");

    using var renameBody = new StringContent("""{"mode":"rename","name":"Native"}""", Encoding.UTF8, "application/json");
    using var rename = await client.PutAsync("/smoke-assets/1", renameBody);
    if (rename.StatusCode != HttpStatusCode.NoContent)
        throw new InvalidOperationException("Native typed PUT case did not bind and dispatch.");
    using var unknownBody = new StringContent("""{"mode":"unknown"}""", Encoding.UTF8, "application/json");
    using var unknown = await client.PutAsync("/smoke-assets/1", unknownBody);
    if (unknown.StatusCode != HttpStatusCode.BadRequest)
        throw new InvalidOperationException("Native typed PUT accepted an unknown selector.");

    using var bindingBody = new StringContent("{\"wire_a\":11,\"wire_b\":22}", Encoding.UTF8, "application/json");
    using var bindingResponse = await client.PostAsync("/smoke-binding", bindingBody);
    var bindingResult = await bindingResponse.Content.ReadAsStringAsync();
    if (bindingResponse.StatusCode != HttpStatusCode.OK || bindingResult != "\"11,22\"")
        throw new InvalidOperationException($"Renamed native JSON members lost constructor identity: {bindingResult}");

    using var invalid = await client.GetAsync("/smoke?name=%20%20");
    if (invalid.StatusCode != HttpStatusCode.BadRequest)
        throw new InvalidOperationException("Native compiled Required validation accepted whitespace.");
    var response = await client.GetStringAsync("/smoke?name=native-aot");
    using var payload = JsonDocument.Parse(response);
    if (payload.RootElement.GetProperty("display_name").GetString() != "native-aot" ||
        payload.RootElement.GetProperty("aliases")[0].GetString() != "native-aot" ||
        payload.RootElement.GetProperty("groups").GetProperty("primary")[0].GetString() != "native-aot" ||
        payload.RootElement.GetProperty("count").GetInt32() != 42 ||
        payload.RootElement.GetProperty("empty_count").ValueKind != JsonValueKind.Null ||
        payload.RootElement.GetProperty("variant").GetProperty("kind").GetString() != "names" ||
        payload.RootElement.GetProperty("variant").GetProperty("names")[0].GetString() != "native-aot")
        throw new InvalidOperationException($"Unexpected response: {response}");

    // Describing an endpoint is the part of the OpenAPI path most easily lost to an AOT-motivated
    // change, so the native run proves the document still names the mapping, not just that the
    // document is served.
    var document = await client.GetStringAsync("/openapi/v1.json");
    if (!document.Contains("\"operationId\": \"smokeRequest\"", StringComparison.Ordinal))
        throw new InvalidOperationException($"Portia endpoint missing from OpenAPI document: {document}");
    using var openApi = JsonDocument.Parse(document);
    var bindingProperties = openApi.RootElement.GetProperty("paths").GetProperty("/smoke-binding").GetProperty("post")
        .GetProperty("requestBody").GetProperty("content").GetProperty("application/json").GetProperty("schema")
        .GetProperty("properties");
    if (!bindingProperties.TryGetProperty("wire_a", out _) || !bindingProperties.TryGetProperty("wire_b", out _))
        throw new InvalidOperationException($"Native OpenAPI lost the resolved JSON member names: {document}");
    var inputExample = openApi.RootElement.GetProperty("paths").GetProperty("/smoke-binding").GetProperty("post")
        .GetProperty("requestBody").GetProperty("content").GetProperty("application/json").GetProperty("examples")
        .GetProperty("binding").GetProperty("value");
    if (inputExample.GetProperty("wire_a").GetInt32() != 11 || inputExample.GetProperty("wire_b").GetInt32() != 22)
        throw new InvalidOperationException("Native typed input examples lost resolver-renamed members.");
    var components = openApi.RootElement.GetProperty("components").GetProperty("schemas");
    var externalSchema = ResolveSchema(openApi.RootElement.GetProperty("paths").GetProperty("/external-contract").GetProperty("get")
        .GetProperty("responses").GetProperty("200").GetProperty("content").GetProperty("application/json").GetProperty("schema"), components);
    if (externalSchema.GetProperty("description").GetString() != "Referenced payload." ||
        externalSchema.GetProperty("title").GetString() != "Referenced payload title" ||
        externalSchema.GetProperty("examples")[0].GetProperty("value").GetString() != "example" ||
        externalSchema.GetProperty("properties").GetProperty("value").GetProperty("description").GetString() != "Referenced value.")
        throw new InvalidOperationException("Native OpenAPI lost descriptions exported by a referenced context factory.");
    var schema = openApi.RootElement.GetProperty("paths").GetProperty("/smoke").GetProperty("get")
        .GetProperty("responses").GetProperty("200").GetProperty("content").GetProperty("application/json")
        .GetProperty("schema");
    var schemaId = schema.GetProperty("$ref").GetString()!.Split('/')[^1];
    var properties = components.GetProperty(schemaId).GetProperty("properties");
    if (components.GetProperty(schemaId).GetProperty("description").GetString() != "Native payload." ||
        properties.GetProperty("display_name").GetProperty("description").GetString() != "Display name." ||
        bindingProperties.GetProperty("wire_a").GetProperty("description").GetString() != "First value.")
        throw new InvalidOperationException("Native descriptions lost CLR member identity.");
    if (!properties.TryGetProperty("display_name", out _) || properties.TryGetProperty("displayName", out _))
        throw new InvalidOperationException($"Portia schema does not match its JSON contract: {document}");
    var aliases = properties.GetProperty("aliases");
    var aliasItemType = aliases.GetProperty("items").GetProperty("type");
    var stringItems = aliasItemType.ValueKind == JsonValueKind.Array
        ? aliasItemType.EnumerateArray().Any(type => type.GetString() == "string")
        : aliasItemType.GetString() == "string";
    if (!aliases.GetProperty("type").EnumerateArray().Any(type => type.GetString() == "array") || !stringItems)
        throw new InvalidOperationException($"Converted collection schema is not a typed array: {document}");
    var groups = ResolveSchema(properties.GetProperty("groups"), components);
    var groupValues = ResolveSchema(groups.GetProperty("additionalProperties"), components);
    if (!HasSchemaType(groups, "object") || groups.TryGetProperty("items", out _) ||
        !HasSchemaType(groupValues, "array") || !HasSchemaType(ResolveSchema(groupValues.GetProperty("items"), components), "string"))
        throw new InvalidOperationException($"Converted dictionary values are not typed arrays: {document}");
    foreach (var name in new[] { "count", "empty_count" })
    {
        var count = ResolveSchema(properties.GetProperty(name), components);
        if (!HasSchemaType(count, "integer") || !HasSchemaType(count, "null") || count.TryGetProperty("items", out _))
            throw new InvalidOperationException($"Nullable converted value schema does not reuse its underlying type mapping: {document}");
    }
    var variants = ResolveSchema(properties.GetProperty("variant"), components).GetProperty("anyOf");
    var namesVariant = variants.EnumerateArray().Select(variant => ResolveSchema(variant, components)).Single(variant =>
        variant.TryGetProperty("properties", out var members) && members.TryGetProperty("names", out _));
    var variantNames = ResolveSchema(namesVariant.GetProperty("properties").GetProperty("names"), components);
    if (!HasSchemaType(variantNames, "array") || HasSchemaType(variantNames, "null") ||
        !HasSchemaType(ResolveSchema(variantNames.GetProperty("items"), components), "string"))
        throw new InvalidOperationException($"Polymorphic converted property is not a typed array: {document}");
    var recursiveId = properties.GetProperty("next_node").GetProperty("properties").GetProperty("next_node").GetProperty("$ref").GetString()!.Split('/')[^1];
    if (!components.GetProperty(recursiveId).GetProperty("properties").TryGetProperty("display_name", out _))
        throw new InvalidOperationException($"Recursive Portia schema does not resolve: {document}");
    var createResponses = openApi.RootElement.GetProperty("paths").GetProperty("/native/resources")
        .GetProperty("post").GetProperty("responses");
    if (createResponses.EnumerateObject().Count(response => response.Name.StartsWith('2')) != 2)
        throw new InvalidOperationException("Bodyless create must document exactly two success responses.");
    foreach (var status in new[] { "200", "201" })
    {
        var createResponse = createResponses.GetProperty(status);
        if (createResponse.TryGetProperty("content", out _) ||
            !createResponse.GetProperty("headers").TryGetProperty("Location", out _))
            throw new InvalidOperationException($"Unexpected bodyless create OpenAPI response: {createResponse}.");
    }

    var uploadOperation = openApi.RootElement.GetProperty("paths").GetProperty("/smoke-upload").GetProperty("post");
    var uploadRequestSchema = uploadOperation.GetProperty("requestBody").GetProperty("content")
        .GetProperty("application/octet-stream").GetProperty("schema");
    var uploadResponseSchema = uploadOperation.GetProperty("responses").GetProperty("200").GetProperty("content")
        .GetProperty("application/octet-stream").GetProperty("schema");
    if (uploadRequestSchema.GetProperty("type").GetString() != "string" ||
        uploadRequestSchema.GetProperty("format").GetString() != "binary" ||
        uploadResponseSchema.GetProperty("type").GetString() != "string" ||
        uploadResponseSchema.GetProperty("format").GetString() != "binary")
        throw new InvalidOperationException($"Custom stream schemas are not binary: {document}");

    using var content = new StreamContent(new MemoryStream([1, 2, 3, 4], writable: false));
    content.Headers.ContentType = new("application/octet-stream");
    using var upload = await client.PostAsync("/smoke-upload", content);
    var uploadResponse = await upload.Content.ReadAsStringAsync();
    if (!upload.IsSuccessStatusCode || uploadResponse != "4")
        throw new InvalidOperationException($"Unexpected custom-body response: {(int)upload.StatusCode} {uploadResponse}");

    var guardProbe = app.Services.GetRequiredService<SmokeGuardProbe>();
    if (guardProbe.Calls != 2)
        throw new InvalidOperationException($"Expected the family-scoped request guard twice, but saw {guardProbe.Calls} calls.");

    // Cross-origin protection trusts exactly the origins the CORS pipeline allows, which the native
    // build decides through the CORS service Portia wraps at startup.
    foreach (var (origin, expected) in new[]
             {
                 ("https://trusted.example", HttpStatusCode.OK),
                 ("https://attacker.example", HttpStatusCode.Forbidden)
             })
    {
        using var crossOrigin = new HttpRequestMessage(HttpMethod.Post, "/smoke-upload")
        {
            Content = new ByteArrayContent([1])
        };
        crossOrigin.Content.Headers.ContentType = new("application/octet-stream");
        crossOrigin.Headers.Add("Sec-Fetch-Site", "cross-site");
        crossOrigin.Headers.Add("Origin", origin);
        using var rejection = await client.SendAsync(crossOrigin);
        if (rejection.StatusCode != expected)
            throw new InvalidOperationException(
                $"Cross-origin upload from {origin} returned {(int)rejection.StatusCode}, expected {(int)expected}.");
    }
}
// This path also runs from freshly packed packages on Linux NativeAOT in CI.
await using (var scope = app.Services.CreateAsyncScope())
{
    var bus = scope.ServiceProvider.GetRequiredService<IRequestBus>();
    IUnaryRequest unary = new SmokeRequest("unary");
    var unaryOutcome = await bus.SendAsync(unary, RequestActor.System);
    if (!unaryOutcome.IsSuccess)
        throw new InvalidOperationException("Native unary dispatch failed.");
    var retained = await bus.SendAsync(new SmokeRequest("retain-continuation"), RequestActor.System);
    if (!retained.IsSuccess)
        throw new InvalidOperationException("Retaining smoke behavior failed.");
    var rejected = false;
    try
    {
        _ = await bus.SendAsync(new SmokeRequest("invoke-retained-continuation"), RequestActor.System);
    }
    catch (InvalidOperationException failure) when (failure.Message ==
        "A request pipeline continuation may be invoked at most once and only during its behavior invocation.")
    {
        rejected = true;
    }
    if (!rejected)
        throw new InvalidOperationException("An expired pipeline continuation adopted a later invocation.");
}
await app.StopAsync();

static JsonElement ResolveSchema(JsonElement schema, JsonElement components)
{
    var visited = new HashSet<string>(StringComparer.Ordinal);
    while (schema.TryGetProperty("$ref", out var reference))
    {
        var target = reference.GetString()!;
        if (!target.StartsWith("#/components/schemas/", StringComparison.Ordinal) || !visited.Add(target))
            throw new InvalidOperationException($"Unexpected schema reference: {target}");
        schema = components.GetProperty(target.Split('/')[^1]);
    }
    return schema;
}

static bool HasSchemaType(JsonElement schema, string expected)
{
    if (!schema.TryGetProperty("type", out var type))
        return false;
    return type.ValueKind == JsonValueKind.Array
        ? type.EnumerateArray().Any(value => value.GetString() == expected)
        : type.GetString() == expected;
}

[Discriminator("portia.smoke.native-aot.request")]
[RequestRoute("portia-smoke", "requests", "native-aot", "get")]
sealed record SmokeRequest([System.ComponentModel.DataAnnotations.Required, System.ComponentModel.DataAnnotations.StringLength(64, MinimumLength = 1)] string Name) : IRequest<SmokePayload>, ICallable, ISmokeRequest;

sealed class SmokeRequestHandler : IRequestHandler<SmokeRequest, SmokePayload>
{
    public ValueTask<Result<SmokePayload>> HandleAsync(IRequestContext<SmokeRequest> context, CancellationToken ct) =>
        ValueTask.FromResult(Result<SmokePayload>.Success(new SmokePayload(context.Request.Name,
            Aliases: new SmokeNames([context.Request.Name]),
            Groups: new Dictionary<string, SmokeNames> { ["primary"] = new([context.Request.Name]) },
            Count: new SmokeCount(42), Variant: new SmokeNamesVariant(new SmokeNames([context.Request.Name])))));
}

sealed class SmokeContinuationProbe
{
    public RequestPipelineNext<SmokePayload>? Retained { get; set; }
}

sealed class SmokeRequestBehavior(SmokeContinuationProbe probe) : IRequestPipelineBehavior<SmokeRequest, SmokePayload>
{
    public ValueTask<Result<SmokePayload>> HandleAsync(IRequestContext<SmokeRequest> context,
        RequestPipelineNext<SmokePayload> continuation, CancellationToken ct)
    {
        if (context.Request.Name == "retain-continuation")
        {
            probe.Retained = continuation;
            return ValueTask.FromResult(Result<SmokePayload>.Success(new SmokePayload(context.Request.Name)));
        }
        return context.Request.Name == "invoke-retained-continuation"
            ? probe.Retained!(ct)
            : continuation(ct);
    }
}

[System.ComponentModel.Description("Native payload.")]
sealed record SmokePayload([property: System.ComponentModel.Description("Display name.")] string DisplayName, SmokePayload? NextNode = null, SmokeNames? Aliases = null,
    Dictionary<string, SmokeNames>? Groups = null, SmokeCount? Count = null, SmokeCount? EmptyCount = null,
    SmokeVariant? Variant = null);

[JsonConverter(typeof(SmokeNamesConverter))]
sealed record SmokeNames(string[] Values);

sealed class SmokeNamesConverter : JsonConverter<SmokeNames>
{
    public override SmokeNames Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(JsonSerializer.Deserialize(ref reader,
            options.GetTypeInfo<string[]>())!);

    public override void Write(Utf8JsonWriter writer, SmokeNames value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value.Values,
            options.GetTypeInfo<string[]>());
}

[JsonConverter(typeof(SmokeCountConverter))]
[System.ComponentModel.Description("Converted count.")]
readonly record struct SmokeCount(int Value);

sealed class SmokeCountConverter : JsonConverter<SmokeCount>
{
    public override SmokeCount Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetInt32());

    public override void Write(Utf8JsonWriter writer, SmokeCount value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value.Value);
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(SmokeNamesVariant), "names")]
[JsonDerivedType(typeof(SmokeTextVariant), "text")]
abstract record SmokeVariant;

sealed record SmokeNamesVariant(SmokeNames Names) : SmokeVariant;

sealed record SmokeTextVariant(string Description) : SmokeVariant;

[Discriminator("portia.smoke.native-aot.upload")]
[RequestRoute("portia-smoke", "requests", "native-aot", "upload")]
sealed record SmokeUpload(int Length) : IRequest<int>, ICallable, ISmokeRequest;

sealed class SmokeUploadHandler : IRequestHandler<SmokeUpload, int>
{
    public ValueTask<Result<int>> HandleAsync(IRequestContext<SmokeUpload> context, CancellationToken ct) =>
        ValueTask.FromResult(Result<int>.Success(context.Request.Length));
}

[Discriminator("portia.smoke.native-aot.quote")]
[RequestRoute("portia-smoke", "requests", "native-aot", "quote")]
sealed record SmokeQuote([property: DecimalStringRange("1", "2", MinimumIsExclusive = true)] DecimalString Amount)
    : IRequest<DecimalString>, ICallable;

sealed class SmokeQuoteHandler : IRequestHandler<SmokeQuote, DecimalString>
{
    public ValueTask<Result<DecimalString>> HandleAsync(IRequestContext<SmokeQuote> context, CancellationToken ct) =>
        ValueTask.FromResult(Result<DecimalString>.Success(context.Request.Amount));
}

[Discriminator("portia.smoke.native-aot.schedule")]
[RequestRoute("portia-smoke", "requests", "native-aot", "schedule")]
sealed record SmokeSchedule(
    [property: JsonPropertyName("plan")] string Mode,
    [property: RequiredWhen(nameof(SmokeSchedule.Mode), "scheduled")] string? Schedule = null) :
    IRequest<string>, ICallable;

sealed class SmokeScheduleHandler : IRequestHandler<SmokeSchedule, string>
{
    public ValueTask<Result<string>> HandleAsync(IRequestContext<SmokeSchedule> context, CancellationToken ct) =>
        ValueTask.FromResult(Result<string>.Success(context.Request.Mode));
}

sealed record SmokeRename(int Id, string Name) : IRequest;

sealed record SmokeArchive(int Id) : IRequest;

sealed class SmokeRenameHandler : IRequestHandler<SmokeRename>
{
    public ValueTask<Result> HandleAsync(IRequestContext<SmokeRename> context, CancellationToken ct) =>
        ValueTask.FromResult(context.Request.Id == 1 && context.Request.Name == "Native"
            ? Result.Success
            : Result.Failure(RequestError.Validation("Unexpected native case values.")));
}

sealed class SmokeArchiveHandler : IRequestHandler<SmokeArchive>
{
    public ValueTask<Result> HandleAsync(IRequestContext<SmokeArchive> context, CancellationToken ct) =>
        ValueTask.FromResult(Result.Success);
}

[Discriminator("portia.smoke.native-aot.create")]
[RequestRoute("portia-smoke", "requests", "native-aot", "create")]
sealed record SmokeCreate(bool Repeated) :
    IRequest<PortiaCreateOutcome<SmokeResource>>, ICallable;

sealed record SmokeResource(string Id);

sealed class SmokeCreateHandler : IRequestHandler<SmokeCreate, PortiaCreateOutcome<SmokeResource>>
{
    public ValueTask<Result<PortiaCreateOutcome<SmokeResource>>> HandleAsync(
        IRequestContext<SmokeCreate> context, CancellationToken ct)
    {
        var resource = new SmokeResource("1");
        var outcome = context.Request.Repeated
            ? PortiaCreateOutcome<SmokeResource>.Repeated(resource)
            : PortiaCreateOutcome<SmokeResource>.Created(resource);
        return ValueTask.FromResult(Result<PortiaCreateOutcome<SmokeResource>>.Success(outcome));
    }
}

[Discriminator("portia.smoke.native-aot.replace")]
[RequestRoute("portia-smoke", "requests", "native-aot", "replace")]
sealed record SmokeReplace(string Resource) : IRequest<PortiaCreateOutcome<SmokeResource>>, ICallable;

sealed class SmokeReplaceHandler : IRequestHandler<SmokeReplace, PortiaCreateOutcome<SmokeResource>>
{
    public ValueTask<Result<PortiaCreateOutcome<SmokeResource>>> HandleAsync(
        IRequestContext<SmokeReplace> context, CancellationToken ct) =>
        ValueTask.FromResult(Result<PortiaCreateOutcome<SmokeResource>>.Success(
            PortiaCreateOutcome<SmokeResource>.Replaced(new SmokeResource(context.Request.Resource))));
}

interface ISmokeRequest : IRequestBase;

sealed class SmokeRequestGuard(SmokeGuardProbe probe) : IRequestGuard<ISmokeRequest>
{
    public ValueTask<Result> GuardAsync(IRequestContext<ISmokeRequest> context, CancellationToken ct)
    {
        probe.Record();
        return ValueTask.FromResult(Result.Success);
    }
}

sealed class SmokeGuardProbe
{
    int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public void Record() => _ = Interlocked.Increment(ref _calls);
}

[PortiaJsonContext]
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(SmokeExternalRequest))]
[JsonSerializable(typeof(SmokeRequest))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(SmokePayload))]
[JsonSerializable(typeof(SmokeNames))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(Dictionary<string, SmokeNames>))]
[JsonSerializable(typeof(SmokeCount))]
[JsonSerializable(typeof(SmokeVariant))]
[JsonSerializable(typeof(SmokeUpload))]
[JsonSerializable(typeof(SmokeQuote))]
[JsonSerializable(typeof(DecimalString))]
[JsonSerializable(typeof(SmokeSchedule))]
[JsonSerializable(typeof(SmokeRename))]
[JsonSerializable(typeof(SmokeArchive))]
[JsonSerializable(typeof(SmokeCreate))]
[JsonSerializable(typeof(SmokeReplace))]
[JsonSerializable(typeof(PortiaCreateOutcome<SmokeResource>))]
[JsonSerializable(typeof(SmokeBinding))]
[JsonSerializable(typeof(SmokeCatalogChanged))]
[JsonSerializable(typeof(SmokeCatalogConfirmed))]
sealed partial class SmokeJsonContext : JsonSerializerContext;

[Discriminator("smoke.catalog.changed", 1)]
sealed record SmokeCatalogChanged : DomainEvent;

[Discriminator("smoke.catalog.confirmed", 1)]
sealed record SmokeCatalogConfirmed : DomainEvent;

[Discriminator("smoke.binding")]
[RequestRoute("public", "smoke", "binding", "read")]
sealed class SmokeBinding(int a, int b) : IRequest<string>, ICallable
{
    public int B { get; } = b;
    public int A { get; } = a;
}

sealed class SmokeBindingHandler : IRequestHandler<SmokeBinding, string>
{
    public ValueTask<Result<string>> HandleAsync(IRequestContext<SmokeBinding> context, CancellationToken ct) =>
        ValueTask.FromResult(Result<string>.Success($"{context.Request.A},{context.Request.B}"));
}

[Discriminator("portia.smoke.external")]
[RequestRoute("portia-smoke", "requests", "external", "get")]
sealed record SmokeExternalRequest : IRequest<App_A.FirstPayload>, ICallable;

sealed class SmokeExternalHandler : IRequestHandler<SmokeExternalRequest, App_A.FirstPayload>
{
    public ValueTask<Result<App_A.FirstPayload>> HandleAsync(IRequestContext<SmokeExternalRequest> context, CancellationToken ct) =>
        ValueTask.FromResult(Result<App_A.FirstPayload>.Success(new App_A.FirstPayload("external")));
}

sealed class SmokeProjector(IProjectionStore store) : IProjectorHandler<SmokeCatalogChanged>, IProjectorHandler<SmokeCatalogConfirmed>
{
    public IProjectionStore Store => store;
    public string Events { get; private set; } = "";

    public ValueTask<Result> HandleAsync(IProjectorContext<SmokeCatalogChanged> context, CancellationToken ct)
    {
        Events += "O";
        return ValueTask.FromResult(Result.Success);
    }

    public ValueTask<Result> HandleAsync(IProjectorContext<SmokeCatalogConfirmed> context, CancellationToken ct)
    {
        Events += "C";
        return ValueTask.FromResult(Result.Success);
    }
}
