using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;

namespace Cntryl.Portia;

/// <summary>Converted wire roots still need metadata for independently bound HTTP body members.</summary>
public sealed class ConvertedRequestBindingTests
{
    /// <summary>A converter prevents the JSON source generator from discovering a request's child types.</summary>
    [Fact]
    public void ShouldRequireChildMetadataGivenConvertedRequestRootOnly()
    {
        var context = new ConvertedRequestRootOnlyJsonContext(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var document = JsonDocument.Parse("""{"body":{"amount":42}}""");

        Assert.NotNull(context.GetTypeInfo(typeof(ConvertedHttpRequest)));
        Assert.Null(context.GetTypeInfo(typeof(ConvertedHttpBody)));
        Assert.Throws<NotSupportedException>(() =>
            PortiaHttpBinding.ReadBody<ConvertedHttpRequest, ConvertedHttpBody>(document.RootElement,
                context.Options, 0, "Body", "body", false, false, null!));
    }

    /// <summary>Explicit child metadata lets generated HTTP binding construct a converted request.</summary>
    [Fact]
    public async Task ShouldBindConvertedRequestGivenExplicitChildMetadata()
    {
        var observed = new ConvertedHttpObservation();
        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        _ = builder.Services.AddSingleton(observed);
        _ = builder.Services.AddPortia().AddHttp().AddRequestHandler<ConvertedHttpHandler>();
        await using var app = builder.Build();
        _ = app.MapPortiaPost<ConvertedHttpRequest>("/converted");
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var content = new StringContent("""{"body":{"amount":42}}""", Encoding.UTF8, "application/json");

        using var response = await client.PostAsync("/converted", content);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(new ConvertedHttpBody(42), observed.Body);
    }
}

[JsonConverter(typeof(ConvertedHttpRequestConverter))]
sealed record ConvertedHttpRequest(ConvertedHttpBody Body) : IRequest, ICallable;

sealed record ConvertedHttpBody(int Amount);

sealed class ConvertedHttpObservation
{
    public ConvertedHttpBody? Body { get; set; }
}

sealed class ConvertedHttpHandler(ConvertedHttpObservation observed) : IRequestHandler<ConvertedHttpRequest>
{
    public ValueTask<Result> HandleAsync(IRequestContext<ConvertedHttpRequest> context, CancellationToken ct)
    {
        observed.Body = context.Request.Body;
        return ValueTask.FromResult(Result.Success);
    }
}

sealed class ConvertedHttpRequestConverter : JsonConverter<ConvertedHttpRequest>
{
    public override ConvertedHttpRequest Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => throw new InvalidOperationException("HTTP binding should deserialize each body member independently.");

    public override void Write(Utf8JsonWriter writer, ConvertedHttpRequest value, JsonSerializerOptions options)
        => throw new InvalidOperationException("This request is only used as HTTP input.");
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(ConvertedHttpRequest))]
sealed partial class ConvertedRequestRootOnlyJsonContext : JsonSerializerContext;

[PortiaJsonContext]
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(ConvertedHttpRequest))]
[JsonSerializable(typeof(ConvertedHttpBody))]
sealed partial class ConvertedRequestWithBodyJsonContext : JsonSerializerContext;
