using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using Cntryl.Portia;

var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:0");
_ = builder.Services.AddSingleton<SmokeGuardProbe>();
_ = builder.Services.AddPortia().ConfigureJson(options => options.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower)
    .AddHttp()
    .AddRequestHandler<SmokeRequestHandler>()
    .AddRequestHandler<SmokeUploadHandler>()
    .AddRequestGuard<SmokeRequestGuard>();
_ = builder.Services.AddCors(options => options.AddPolicy("smoke", policy => policy.WithOrigins("https://trusted.example")));
var app = builder.Build();
_ = app.UseCors("smoke");
_ = app.MapPortiaOpenApi();
_ = app.MapPortiaGet<SmokeRequest, SmokePayload>("/smoke");
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
await app.StartAsync();
using (var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) })
{
    var response = await client.GetStringAsync("/smoke?name=native-aot");
    using var payload = JsonDocument.Parse(response);
    if (payload.RootElement.GetProperty("display_name").GetString() != "native-aot")
        throw new InvalidOperationException($"Unexpected response: {response}");

    // Describing an endpoint is the part of the OpenAPI path most easily lost to an AOT-motivated
    // change, so the native run proves the document still names the mapping, not just that the
    // document is served.
    var document = await client.GetStringAsync("/openapi/v1.json");
    if (!document.Contains("\"operationId\": \"smokeRequest\"", StringComparison.Ordinal))
        throw new InvalidOperationException($"Portia endpoint missing from OpenAPI document: {document}");
    using var openApi = JsonDocument.Parse(document);
    var components = openApi.RootElement.GetProperty("components").GetProperty("schemas");
    var schema = openApi.RootElement.GetProperty("paths").GetProperty("/smoke").GetProperty("get")
        .GetProperty("responses").GetProperty("200").GetProperty("content").GetProperty("application/json")
        .GetProperty("schema");
    var schemaId = schema.GetProperty("$ref").GetString()!.Split('/')[^1];
    var properties = components.GetProperty(schemaId).GetProperty("properties");
    if (!properties.TryGetProperty("display_name", out _) || properties.TryGetProperty("displayName", out _))
        throw new InvalidOperationException($"Portia schema does not match its JSON contract: {document}");
    var recursiveId = properties.GetProperty("next_node").GetProperty("properties").GetProperty("next_node").GetProperty("$ref").GetString()!.Split('/')[^1];
    if (!components.GetProperty(recursiveId).GetProperty("properties").TryGetProperty("display_name", out _))
        throw new InvalidOperationException($"Recursive Portia schema does not resolve: {document}");
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
await app.StopAsync();

[Discriminator("portia.smoke.native-aot.request")]
[RequestRoute("portia-smoke", "requests", "native-aot", "get")]
sealed record SmokeRequest(string Name) : IRequest<SmokePayload>, ICallable, ISmokeRequest;

sealed class SmokeRequestHandler : IRequestHandler<SmokeRequest, SmokePayload>
{
    public ValueTask<Result<SmokePayload>> HandleAsync(IRequestContext<SmokeRequest> context, CancellationToken ct) =>
        ValueTask.FromResult(Result<SmokePayload>.Success(new SmokePayload(context.Request.Name)));
}

sealed record SmokePayload(string DisplayName, SmokePayload? NextNode = null);

[Discriminator("portia.smoke.native-aot.upload")]
[RequestRoute("portia-smoke", "requests", "native-aot", "upload")]
sealed record SmokeUpload(int Length) : IRequest<int>, ICallable, ISmokeRequest;

sealed class SmokeUploadHandler : IRequestHandler<SmokeUpload, int>
{
    public ValueTask<Result<int>> HandleAsync(IRequestContext<SmokeUpload> context, CancellationToken ct) =>
        ValueTask.FromResult(Result<int>.Success(context.Request.Length));
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
[JsonSerializable(typeof(SmokeRequest))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(SmokePayload))]
[JsonSerializable(typeof(SmokeUpload))]
sealed partial class SmokeJsonContext : JsonSerializerContext;
