using System.Text.Json;
using System.Text.Json.Serialization;
using Cntryl.Portia;

var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:0");
_ = builder.Services.AddPortia().AddRequestHandler<SmokeRequestHandler>();
var app = builder.Build();
_ = app.MapPortiaGet<SmokeRequest, string>("/smoke");
await app.StartAsync();
using (var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) })
{
    var response = await client.GetStringAsync("/smoke?name=native-aot");
    if (response != "\"native-aot\"")
        throw new InvalidOperationException($"Unexpected response: {response}");
}
await app.StopAsync();

[Discriminator("portia.smoke.native-aot.request")]
[RequestRoute("portia-smoke", "requests", "native-aot", "get")]
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
