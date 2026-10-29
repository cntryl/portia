using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cntryl.Portia;

[JsonConverter(typeof(OpenApiSelectionListConverter))]
sealed record OpenApiSelectionList(OpenApiSelection[] Values);

sealed record OpenApiSelection(string ItemId, long Revision, OpenApiSelection? Next = null);

sealed record OpenApiSelectionSchema(string ItemId, long Revision, OpenApiSelection? Next = null);

sealed class OpenApiSelectionListConverter : JsonConverter<OpenApiSelectionList>
{
    public override OpenApiSelectionList Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options) => new(JsonSerializer.Deserialize(ref reader,
        (System.Text.Json.Serialization.Metadata.JsonTypeInfo<OpenApiSelection[]>)options.GetTypeInfo(typeof(OpenApiSelection[])))!);

    public override void Write(Utf8JsonWriter writer, OpenApiSelectionList value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value.Values,
            (System.Text.Json.Serialization.Metadata.JsonTypeInfo<OpenApiSelection[]>)options.GetTypeInfo(typeof(OpenApiSelection[])));
}

[Discriminator("test.openapi.converted-root")]
[RequestRoute("test", "openapi", "converted-root", "echo")]
[JsonConverter(typeof(OpenApiRootSelectionRequestConverter))]
sealed record OpenApiRootSelectionRequest(OpenApiSelection[] Values) : IRequest<OpenApiSelectionList?>, ICallable;

sealed class OpenApiRootSelectionRequestConverter : JsonConverter<OpenApiRootSelectionRequest>
{
    public override OpenApiRootSelectionRequest Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options) => new(JsonSerializer.Deserialize(ref reader,
        (System.Text.Json.Serialization.Metadata.JsonTypeInfo<OpenApiSelection[]>)options.GetTypeInfo(typeof(OpenApiSelection[])))!);

    public override void Write(Utf8JsonWriter writer, OpenApiRootSelectionRequest value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value.Values,
            (System.Text.Json.Serialization.Metadata.JsonTypeInfo<OpenApiSelection[]>)options.GetTypeInfo(typeof(OpenApiSelection[])));
}

sealed class OpenApiRootSelectionHandler : IRequestHandler<OpenApiRootSelectionRequest, OpenApiSelectionList?>
{
    public ValueTask<Result<OpenApiSelectionList?>> HandleAsync(IRequestContext<OpenApiRootSelectionRequest> context,
        CancellationToken ct)
    {
        var result = context.Request.Values.Length == 0
            ? null
            : new OpenApiSelectionList(context.Request.Values);
        return ValueTask.FromResult(Result<OpenApiSelectionList?>.Success(result));
    }
}

sealed record OpenApiSelectionEnvelope(OpenApiSelectionList Items, OpenApiSelectionList? OptionalItems = null);

[Discriminator("test.openapi.converted")]
[RequestRoute("test", "openapi", "converted", "echo")]
sealed record OpenApiConvertedRequest(OpenApiSelectionEnvelope Payload) : IRequest<OpenApiSelectionList>, ICallable;

sealed class OpenApiConvertedHandler : IRequestHandler<OpenApiConvertedRequest, OpenApiSelectionList>
{
    public ValueTask<Result<OpenApiSelectionList>> HandleAsync(IRequestContext<OpenApiConvertedRequest> context,
        CancellationToken ct) => ValueTask.FromResult(Result<OpenApiSelectionList>.Success(context.Request.Payload.Items));
}

sealed record OpenApiMissingRepresentation(string Value);
