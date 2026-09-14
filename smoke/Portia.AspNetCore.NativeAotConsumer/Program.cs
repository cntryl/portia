using System.Text.Json;
using System.Text.Json.Serialization;
using Cntryl.Portia;

var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:0");
_ = builder.Services.AddPortia().ConfigureJson(options => options.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower).AddHttp().AddRequestHandler<SmokeRequestHandler>();
var app = builder.Build();
_ = app.MapPortiaOpenApi();
_ = app.MapPortiaGet<SmokeRequest, SmokePayload>("/smoke");
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
}
await app.StopAsync();

[Discriminator("portia.smoke.native-aot.request")]
[RequestRoute("portia-smoke", "requests", "native-aot", "get")]
sealed record SmokeRequest(string Name) : IRequest<SmokePayload>, ICallable;

sealed class SmokeRequestHandler : IRequestHandler<SmokeRequest, SmokePayload>
{
    public ValueTask<Result<SmokePayload>> HandleAsync(IRequestContext<SmokeRequest> context, CancellationToken ct) =>
        ValueTask.FromResult(Result<SmokePayload>.Success(new SmokePayload(context.Request.Name)));
}

sealed record SmokePayload(string DisplayName, SmokePayload? NextNode = null);

[PortiaJsonContext]
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(SmokeRequest))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(SmokePayload))]
sealed partial class SmokeJsonContext : JsonSerializerContext;
