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
