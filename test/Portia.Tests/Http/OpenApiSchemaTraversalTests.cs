using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;

namespace Cntryl.Portia;

/// <summary>Schema mappings follow dictionary values and polymorphic contracts over HTTP.</summary>
public sealed class OpenApiSchemaTraversalTests : IAsyncDisposable
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

    /// <summary>Dictionary values describe the array actually read and written by their converter.</summary>
    [Theory]
    [InlineData("json")]
    [InlineData("yml")]
    public async Task ShouldDescribeConvertedDictionaryValuesGivenDictionaryContract(string format)
    {
        var builder = CreateBuilder();
        _ = builder.Services.AddPortia().AddHttp(options =>
                options.OpenApiSchemaType = type => type == typeof(OpenApiTraversalCollection)
                    ? typeof(OpenApiTraversalItem[])
                    : null)
            .AddRequestHandler<OpenApiDictionaryTraversalHandler>();
        _app = builder.Build();
        _ = _app.MapPortiaOpenApi();
        _ = _app.MapPortiaPost<OpenApiDictionaryTraversalRequest, Dictionary<string, OpenApiTraversalCollection>>("/dictionary-traversal");
        await _app.StartAsync();
        using var client = _app.GetTestClient();

        using var response = await client.PostAsync("/dictionary-traversal", new StringContent(
            """{"payload":{"first":[{"item_id":"root","next":{"item_id":"child"}}],"second":[{"item_id":"sibling"}]}}""",
            Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
        using var actual = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("child", actual.RootElement.GetProperty("first")[0].GetProperty("next").GetProperty("item_id").GetString());
        Assert.Equal("sibling", actual.RootElement.GetProperty("second")[0].GetProperty("item_id").GetString());

        var operation = (await ReadDocumentAsync(client, format)).Paths!["/dictionary-traversal"].Operations![HttpMethod.Post];
        var request = operation.RequestBody!.Content!["application/json"].Schema!.Properties!["payload"];
        var result = operation.Responses!["200"].Content!["application/json"].Schema!;
        foreach (var dictionary in new[] { request, result })
        {
            Assert.True(dictionary.Type!.Value.HasFlag(JsonSchemaType.Object));
            Assert.Null(dictionary.Items);
            AssertCollectionSchema(dictionary.AdditionalProperties!);
        }
    }

    /// <summary>Each polymorphic branch retains its own mapped properties and discriminator.</summary>
    [Theory]
    [InlineData("json")]
    [InlineData("yml")]
    public async Task ShouldDescribeConvertedVariantPropertiesGivenPolymorphicContract(string format)
    {
        var builder = CreateBuilder();
        _ = builder.Services.AddPortia().AddHttp(options =>
                options.OpenApiSchemaType = type => type == typeof(OpenApiTraversalCollection)
                    ? typeof(OpenApiTraversalItem[])
                    : null)
            .AddRequestHandler<OpenApiPolymorphicTraversalHandler>();
        _app = builder.Build();
        _ = _app.MapPortiaOpenApi();
        _ = _app.MapPortiaPost<OpenApiPolymorphicTraversalRequest, OpenApiTraversalVariant>("/polymorphic-traversal");
        await _app.StartAsync();
        using var client = _app.GetTestClient();

        using var response = await client.PostAsync("/polymorphic-traversal", new StringContent(
            """{"payload":{"kind":"collection","items":[{"item_id":"variant","next":{"item_id":"nested"}}],"groups":{"first":[{"item_id":"group"}]},"batches":[[{"item_id":"batch"}]]}}""",
            Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
        using var actual = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("collection", actual.RootElement.GetProperty("kind").GetString());
        Assert.Equal("nested", actual.RootElement.GetProperty("items")[0].GetProperty("next").GetProperty("item_id").GetString());
        Assert.Equal("group", actual.RootElement.GetProperty("groups").GetProperty("first")[0].GetProperty("item_id").GetString());
        Assert.Equal("batch", actual.RootElement.GetProperty("batches")[0][0].GetProperty("item_id").GetString());

        var operation = (await ReadDocumentAsync(client, format)).Paths!["/polymorphic-traversal"].Operations![HttpMethod.Post];
        var request = operation.RequestBody!.Content!["application/json"].Schema!.Properties!["payload"];
        var result = operation.Responses!["200"].Content!["application/json"].Schema!;
        foreach (var polymorphic in new[] { request, result })
        {
            Assert.Equal(2, polymorphic.AnyOf!.Count);
            var collection = Assert.Single(polymorphic.AnyOf, branch => branch.Properties!.ContainsKey("items"));
            Assert.Equal("collection", collection.Properties!["kind"].Const);
            Assert.Contains("items", collection.Required!);
            AssertCollectionSchema(collection.Properties["items"]);
            var groups = collection.Properties["groups"];
            Assert.Null(groups.Items);
            AssertCollectionSchema(groups.AdditionalProperties!);
            AssertCollectionSchema(collection.Properties["batches"].Items!);
            var text = Assert.Single(polymorphic.AnyOf, branch => branch.Properties!.ContainsKey("description"));
            Assert.Equal(JsonSchemaType.String, text.Properties!["description"].Type);
            Assert.Equal("text", text.Properties["kind"].Const);
        }
    }

    /// <summary>A nullable value contract reuses the mapping of its underlying converter-backed type.</summary>
    [Theory]
    [InlineData("json")]
    [InlineData("yml")]
    public async Task ShouldDescribeNullableConvertedValuesGivenUnderlyingTypeMapping(string format)
    {
        var builder = CreateBuilder();
        _ = builder.Services.AddPortia().AddHttp(options =>
                options.OpenApiSchemaType = type => type == typeof(OpenApiTraversalCount) ? typeof(long) : null)
            .AddRequestHandler<OpenApiNullableTraversalHandler>();
        _app = builder.Build();
        _ = _app.MapPortiaOpenApi();
        _ = _app.MapPortiaPost<OpenApiNullableTraversalRequest, OpenApiTraversalCounts>("/nullable-traversal");
        await _app.StartAsync();
        using var client = _app.GetTestClient();

        foreach (var optional in new[] { "43", "null" })
        {
            using var response = await client.PostAsync("/nullable-traversal", new StringContent(
                "{\"payload\":{\"required_count\":42,\"optional_count\":" + optional + "}}",
                Encoding.UTF8, "application/json"));
            response.EnsureSuccessStatusCode();
            using var actual = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(42, actual.RootElement.GetProperty("required_count").GetInt64());
            Assert.Equal(optional, actual.RootElement.GetProperty("optional_count").GetRawText());
        }

        var operation = (await ReadDocumentAsync(client, format)).Paths!["/nullable-traversal"].Operations![HttpMethod.Post];
        var request = operation.RequestBody!.Content!["application/json"].Schema!.Properties!["payload"];
        var result = operation.Responses!["200"].Content!["application/json"].Schema!;
        foreach (var envelope in new[] { request, result })
        {
            Assert.Contains("required_count", envelope.Required!);
            Assert.DoesNotContain("optional_count", envelope.Required!);
            var required = envelope.Properties!["required_count"];
            Assert.True(required.Type.HasValue);
            Assert.True(required.Type!.Value.HasFlag(JsonSchemaType.Integer));
            Assert.False(required.Type.Value.HasFlag(JsonSchemaType.Null));
            Assert.Null(required.Items);
            var optional = envelope.Properties["optional_count"];
            Assert.True(optional.Type.HasValue);
            Assert.True(optional.Type!.Value.HasFlag(JsonSchemaType.Integer));
            Assert.True(optional.Type.Value.HasFlag(JsonSchemaType.Null));
            Assert.Null(optional.Items);
        }
    }

    /// <summary>Recursive wire representations resolve references after escaped property paths are rebased.</summary>
    [Theory]
    [InlineData("json")]
    [InlineData("yml")]
    public async Task ShouldDescribeRecursiveConvertedArraysGivenEscapedPropertyName(string format)
    {
        var builder = CreateBuilder();
        _ = builder.Services.AddPortia().AddHttp(options =>
            {
                options.OpenApiSchemaType = type => type == typeof(OpenApiRecursiveArray)
                    ? typeof(OpenApiRecursiveArray[])
                    : null;
                options.OpenApiSchemaCollectionElementIsNullable = (contractType, elementType) =>
                    contractType == typeof(OpenApiRecursiveArray) && elementType == typeof(OpenApiRecursiveArray)
                        ? true
                        : null;
            })
            .AddRequestHandler<OpenApiRecursiveTraversalHandler>();
        _app = builder.Build();
        _ = _app.MapPortiaOpenApi();
        _ = _app.MapPortiaPost<OpenApiRecursiveTraversalRequest, OpenApiRecursiveArray>("/recursive-traversal");
        await _app.StartAsync();
        using var client = _app.GetTestClient();

        using var response = await client.PostAsync("/recursive-traversal", new StringContent(
            """{"payload":{"a~/b":[[],[[]],null]}}""", Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
        using var actual = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("[[],[[]],null]", actual.RootElement.GetRawText());

        var serialized = await client.GetStringAsync("/openapi/v1." + format);
        var document = ReadDocument(serialized, format);
        var operation = document.Paths!["/recursive-traversal"].Operations![HttpMethod.Post];
        var envelope = operation.RequestBody!.Content!["application/json"].Schema!.Properties!["payload"];
        Assert.False(envelope.Required?.Contains("a~/b") ?? false);
        var requestTree = envelope.Properties!["a~/b"];
        Assert.Equal(JsonSchemaType.Array | JsonSchemaType.Null, requestTree.Type);
        AssertRecursiveArraySchema(requestTree);
        AssertRecursiveArraySchema(operation.Responses!["200"].Content!["application/json"].Schema!,
            rootNullable: false);

        if (format == "json")
        {
            using var raw = JsonDocument.Parse(serialized);
            var rawTree = raw.RootElement.GetProperty("components").GetProperty("schemas")
                .EnumerateObject().Select(component => component.Value)
                .First(schema => schema.TryGetProperty("properties", out var properties) &&
                                 properties.TryGetProperty("a~/b", out _))
                .GetProperty("properties").GetProperty("a~/b");
            Assert.True(rawTree.TryGetProperty("default", out var declaredDefault));
            Assert.Equal(JsonValueKind.Null, declaredDefault.ValueKind);
        }
        else
        {
            Assert.Contains("default: null", serialized, StringComparison.Ordinal);
        }
    }

    static WebApplicationBuilder CreateBuilder()
    {
        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        return builder;
    }

    static async Task<OpenApiDocument> ReadDocumentAsync(HttpClient client, string format) =>
        ReadDocument(await client.GetStringAsync("/openapi/v1." + format), format);

    static OpenApiDocument ReadDocument(string serialized, string format)
    {
        var settings = new OpenApiReaderSettings();
        settings.AddYamlReader();
        var parsed = OpenApiDocument.Parse(serialized, format == "yml" ? "yaml" : "json", settings);
        Assert.Empty(parsed.Diagnostic!.Errors);
        return parsed.Document!;
    }

    static void AssertCollectionSchema(IOpenApiSchema collection)
    {
        Assert.NotNull(collection);
        Assert.True(collection.Type.HasValue);
        Assert.True(collection.Type!.Value.HasFlag(JsonSchemaType.Array));
        Assert.Equal(JsonSchemaType.String, collection.Items!.Properties!["item_id"].Type);
        Assert.Contains("item_id", collection.Items.Required!);
        var child = collection.Items.Properties["next"];
        Assert.True(child.Type!.Value.HasFlag(JsonSchemaType.Null));
        Assert.Equal(JsonSchemaType.String, child.Properties!["item_id"].Type);
        Assert.Contains("item_id", child.Properties["next"].Properties!.Keys);
    }

    static void AssertRecursiveArraySchema(IOpenApiSchema array, bool rootNullable = true)
    {
        Assert.Equal(rootNullable ? JsonSchemaType.Array | JsonSchemaType.Null : JsonSchemaType.Array, array.Type);
        Assert.Equal(2, array.Items!.AnyOf!.Count);
        var recursive = Assert.Single(array.Items.AnyOf, branch => branch is OpenApiSchemaReference);
        Assert.Equal(rootNullable ? JsonSchemaType.Array | JsonSchemaType.Null : JsonSchemaType.Array,
            recursive.Type);
        Assert.NotNull(recursive.Items!.AnyOf);
        Assert.Single(array.Items.AnyOf, branch => branch.Type == JsonSchemaType.Null);
    }
}
