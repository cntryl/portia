using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Cntryl.Portia;

[JsonConverter(typeof(OpenApiTraversalCollectionConverter))]
sealed record OpenApiTraversalCollection(OpenApiTraversalItem[] Values);

sealed record OpenApiTraversalItem(string ItemId, OpenApiTraversalItem? Next = null);

sealed class OpenApiTraversalCollectionConverter : JsonConverter<OpenApiTraversalCollection>
{
    public override OpenApiTraversalCollection Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options) => new(JsonSerializer.Deserialize(ref reader,
        (JsonTypeInfo<OpenApiTraversalItem[]>)options.GetTypeInfo(typeof(OpenApiTraversalItem[])))!);

    public override void Write(Utf8JsonWriter writer, OpenApiTraversalCollection value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value.Values,
            (JsonTypeInfo<OpenApiTraversalItem[]>)options.GetTypeInfo(typeof(OpenApiTraversalItem[])));
}

[Discriminator("test.openapi.dictionary-traversal")]
[RequestRoute("test", "openapi", "dictionary-traversal", "echo")]
sealed record OpenApiDictionaryTraversalRequest(Dictionary<string, OpenApiTraversalCollection> Payload)
    : IRequest<Dictionary<string, OpenApiTraversalCollection>>, ICallable;

sealed class OpenApiDictionaryTraversalHandler
    : IRequestHandler<OpenApiDictionaryTraversalRequest, Dictionary<string, OpenApiTraversalCollection>>
{
    public ValueTask<Result<Dictionary<string, OpenApiTraversalCollection>>> HandleAsync(
        IRequestContext<OpenApiDictionaryTraversalRequest> context, CancellationToken ct) =>
        ValueTask.FromResult(Result<Dictionary<string, OpenApiTraversalCollection>>.Success(context.Request.Payload));
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(OpenApiTraversalCollectionVariant), "collection")]
[JsonDerivedType(typeof(OpenApiTraversalTextVariant), "text")]
abstract record OpenApiTraversalVariant;

sealed record OpenApiTraversalCollectionVariant(OpenApiTraversalCollection Items,
    Dictionary<string, OpenApiTraversalCollection> Groups, List<OpenApiTraversalCollection> Batches)
    : OpenApiTraversalVariant;

sealed record OpenApiTraversalTextVariant(string Description) : OpenApiTraversalVariant;

[Discriminator("test.openapi.polymorphic-traversal")]
[RequestRoute("test", "openapi", "polymorphic-traversal", "echo")]
sealed record OpenApiPolymorphicTraversalRequest(OpenApiTraversalVariant Payload)
    : IRequest<OpenApiTraversalVariant>, ICallable;

sealed class OpenApiPolymorphicTraversalHandler : IRequestHandler<OpenApiPolymorphicTraversalRequest, OpenApiTraversalVariant>
{
    public ValueTask<Result<OpenApiTraversalVariant>> HandleAsync(
        IRequestContext<OpenApiPolymorphicTraversalRequest> context, CancellationToken ct) =>
        ValueTask.FromResult(Result<OpenApiTraversalVariant>.Success(context.Request.Payload));
}

[JsonConverter(typeof(OpenApiTraversalCountConverter))]
readonly record struct OpenApiTraversalCount(long Value);

sealed class OpenApiTraversalCountConverter : JsonConverter<OpenApiTraversalCount>
{
    public override OpenApiTraversalCount Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options) => new(reader.GetInt64());

    public override void Write(Utf8JsonWriter writer, OpenApiTraversalCount value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value.Value);
}

sealed record OpenApiTraversalCounts(OpenApiTraversalCount RequiredCount, OpenApiTraversalCount? OptionalCount = null);

[Discriminator("test.openapi.nullable-traversal")]
[RequestRoute("test", "openapi", "nullable-traversal", "echo")]
sealed record OpenApiNullableTraversalRequest(OpenApiTraversalCounts Payload) : IRequest<OpenApiTraversalCounts>, ICallable;

sealed class OpenApiNullableTraversalHandler : IRequestHandler<OpenApiNullableTraversalRequest, OpenApiTraversalCounts>
{
    public ValueTask<Result<OpenApiTraversalCounts>> HandleAsync(
        IRequestContext<OpenApiNullableTraversalRequest> context, CancellationToken ct) =>
        ValueTask.FromResult(Result<OpenApiTraversalCounts>.Success(context.Request.Payload));
}

[JsonConverter(typeof(OpenApiRecursiveArrayConverter))]
sealed record OpenApiRecursiveArray(OpenApiRecursiveArray?[] Children);

sealed class OpenApiRecursiveArrayConverter : JsonConverter<OpenApiRecursiveArray>
{
    public override OpenApiRecursiveArray Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options) => new(JsonSerializer.Deserialize(ref reader,
        (JsonTypeInfo<OpenApiRecursiveArray?[]>)options.GetTypeInfo(typeof(OpenApiRecursiveArray[])))!);

    public override void Write(Utf8JsonWriter writer, OpenApiRecursiveArray value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value.Children,
            (JsonTypeInfo<OpenApiRecursiveArray?[]>)options.GetTypeInfo(typeof(OpenApiRecursiveArray[])));
}

sealed record OpenApiRecursiveArrayEnvelope([property: JsonPropertyName("a~/b")] OpenApiRecursiveArray? Tree = null);

[Discriminator("test.openapi.recursive-traversal")]
[RequestRoute("test", "openapi", "recursive-traversal", "echo")]
sealed record OpenApiRecursiveTraversalRequest(OpenApiRecursiveArrayEnvelope Payload) : IRequest<OpenApiRecursiveArray>, ICallable;

sealed class OpenApiRecursiveTraversalHandler : IRequestHandler<OpenApiRecursiveTraversalRequest, OpenApiRecursiveArray>
{
    public ValueTask<Result<OpenApiRecursiveArray>> HandleAsync(
        IRequestContext<OpenApiRecursiveTraversalRequest> context, CancellationToken ct) =>
        ValueTask.FromResult(Result<OpenApiRecursiveArray>.Success(context.Request.Payload.Tree!));
}

sealed record OpenApiMappedCollectionNullability(List<OpenApiSelection?> Items,
    Dictionary<string, OpenApiSelection?> Values, List<string> Labels);

[Discriminator("test.openapi.collection-nullability")]
[RequestRoute("test", "openapi", "collection-nullability", "echo")]
sealed record OpenApiCollectionNullabilityRequest(OpenApiMappedCollectionNullability Payload)
    : IRequest<OpenApiMappedCollectionNullability>, ICallable;

sealed class OpenApiCollectionNullabilityHandler
    : IRequestHandler<OpenApiCollectionNullabilityRequest, OpenApiMappedCollectionNullability>
{
    public ValueTask<Result<OpenApiMappedCollectionNullability>> HandleAsync(
        IRequestContext<OpenApiCollectionNullabilityRequest> context, CancellationToken ct) =>
        ValueTask.FromResult(Result<OpenApiMappedCollectionNullability>.Success(context.Request.Payload));
}

[PortiaJsonContext]
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(OpenApiDictionaryTraversalRequest))]
[JsonSerializable(typeof(Dictionary<string, OpenApiTraversalCollection>))]
[JsonSerializable(typeof(OpenApiPolymorphicTraversalRequest))]
[JsonSerializable(typeof(OpenApiTraversalVariant))]
[JsonSerializable(typeof(OpenApiTraversalCollection))]
[JsonSerializable(typeof(OpenApiTraversalItem[]))]
[JsonSerializable(typeof(OpenApiNullableTraversalRequest))]
[JsonSerializable(typeof(OpenApiTraversalCounts))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(OpenApiRecursiveTraversalRequest))]
[JsonSerializable(typeof(OpenApiRecursiveArrayEnvelope))]
[JsonSerializable(typeof(OpenApiRecursiveArray))]
[JsonSerializable(typeof(OpenApiRecursiveArray[]))]
[JsonSerializable(typeof(OpenApiCollectionNullabilityRequest))]
[JsonSerializable(typeof(OpenApiMappedCollectionNullability))]
[JsonSerializable(typeof(OpenApiSelection))]
[JsonSerializable(typeof(OpenApiSelectionSchema))]
sealed partial class OpenApiTraversalJsonContext : JsonSerializerContext;
