using System.Text.Json;
using System.Text.Json.Serialization;
using Cntryl.Portia;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:0");
_ = builder.Services.AddPortia()
    .AddRequestHandler<SmokeRequestHandler>();

var app = builder.Build();
_ = app.MapPortiaGet<SmokeRequest, string>("/smoke");

await app.StartAsync();
using (var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) })
{
    var response = await client.GetStringAsync("/smoke?name=package");
    if (response != "\"package\"")
        throw new InvalidOperationException($"Unexpected response: {response}");

    // The packaged generator must still describe its endpoints: a mapping that dispatches
    // correctly but never reaches the document is the shape of a regression this smoke run
    // exists to catch.
    var document = await client.GetStringAsync("/openapi/v1.json");
    if (!document.Contains("\"operationId\": \"smokeRequest\"", StringComparison.Ordinal))
        throw new InvalidOperationException($"Portia endpoint missing from OpenAPI document: {document}");
}

await app.StopAsync();

[Discriminator("portia.smoke.request")]
[RequestRoute("portia-smoke", "requests", "package", "get")]
sealed record SmokeRequest(string Name) : IRequest<string>, ICallable;

sealed class SmokeRequestHandler : IRequestHandler<SmokeRequest, string>
{
    public ValueTask<Result<string>> HandleAsync(IRequestContext<SmokeRequest> context, CancellationToken ct) =>
        ValueTask.FromResult(Result<string>.Success(context.Request.Name));
}

[PortiaJsonContext]
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(SmokeRequest))]
[JsonSerializable(typeof(string))]
sealed partial class SmokeJsonContext : JsonSerializerContext;
