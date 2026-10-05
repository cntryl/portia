using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Json.Schema;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Cntryl.Portia.Tests;

[Collection("MCP HTTP integration")]
public sealed class McpSchemaFidelityTests
{
    [Theory]
    [InlineData("schema.nullable", true)]
    [InlineData("schema.no-result", false)]
    [InlineData("trees.read", true)]
    public async Task ShouldValidateSuccessfulNullableRecursiveAndNoResultTools(string name, bool structured)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        _ = builder.Services.AddPortia().AddRequestHandler<NullableReplyHandler>().AddMcpTool<NullableReply>()
            .AddRequestHandler<NoResultHandler>().AddMcpTool<NoResult>()
            .AddRequestHandler<McpRegistrationTests.ReadTreeHandler>().AddMcpTool<McpRegistrationTests.ReadTree>()
            .AddMcpHttp();
        await using var app = builder.Build();
        app.Use((context, next) =>
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "schema-actor")], "test"));
            return next(context);
        });
        _ = app.MapPortiaMcp().AllowAnonymous();
        await app.StartAsync();
        await using var transport = new HttpClientTransport(new HttpClientTransportOptions
        { Endpoint = new Uri("http://localhost/mcp") }, app.GetTestClient());
        await using var client = await McpClient.CreateAsync(transport);
        var tool = Assert.Single(await client.ListToolsAsync(), candidate => candidate.Name == name);
        var result = await client.CallToolAsync(name, new Dictionary<string, object?>());
        Assert.False(result.IsError);
        if (!structured)
        {
            Assert.Null(tool.ProtocolTool.OutputSchema);
            Assert.Null(result.StructuredContent);
            Assert.NotEmpty(Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
            return;
        }
        var schema = JsonSchema.Build(Assert.IsType<JsonElement>(tool.ProtocolTool.OutputSchema),
            new BuildOptions { Dialect = Dialect.Draft202012, SchemaRegistry = new() });
        Assert.True(schema.Evaluate(Assert.IsType<JsonElement>(result.StructuredContent)).IsValid);
        if (name == "schema.nullable")
            Assert.Equal(JsonValueKind.Null, result.StructuredContent!.Value.GetProperty("result").ValueKind);
        else
            Assert.Contains("#/properties/result", tool.ProtocolTool.OutputSchema!.Value.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public void ShouldKeepProtocolContractsBoundToTheirConfiguredJsonOptions()
    {
        var naming = JsonNamingPolicy.CamelCase;
        var services = new ServiceCollection();
        _ = services.AddPortia().ConfigureJson(json => json.PropertyNamingPolicy = naming)
            .AddRequestHandler<SchemaProbeHandler>().AddMcpTool<SchemaProbe>();
        using var first = services.BuildServiceProvider();
        var firstSchema = Assert.Single(first.GetServices<McpServerTool>()).ProtocolTool.InputSchema;
        naming = JsonNamingPolicy.SnakeCaseLower;
        using var second = services.BuildServiceProvider();
        var secondSchema = Assert.Single(second.GetServices<McpServerTool>()).ProtocolTool.InputSchema;
        Assert.True(firstSchema.GetProperty("properties").GetProperty("nested").GetProperty("properties")
            .TryGetProperty("sourceRecordId", out _));
        Assert.True(secondSchema.GetProperty("properties").GetProperty("nested").GetProperty("properties")
            .TryGetProperty("source_record_id", out _));
    }

    [Fact]
    public void ShouldRejectConverterShapedToolInputBeforeServing()
    {
        var services = new ServiceCollection();
        _ = services.AddPortia().AddRequestHandler<ScalarProbeHandler>().AddMcpTool<ScalarProbe>();
        using var provider = services.BuildServiceProvider();
        var error = Assert.Throws<InvalidOperationException>(() => provider.GetServices<McpServerTool>().ToArray());
        Assert.Contains("object", error.Message, StringComparison.Ordinal);
        Assert.Contains("schema.scalar", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{}", false)]
    [InlineData("{\"display_name\":null,\"nested\":{\"value\":1},\"values\":[]}", false)]
    [InlineData("{\"display_name\":\"ok\",\"nested\":{},\"values\":[]}", false)]
    [InlineData("{\"display_name\":\"ok\",\"nested\":{\"value\":1},\"values\":[]}", false)]
    [InlineData("{\"inherited\":\"base\",\"display_name\":\"ok\",\"nested\":{\"value\":1},\"values\":[]}", true)]
    [InlineData("{\"inherited\":\"base\",\"display_name\":\"ok\",\"nested\":{\"value\":1},\"values\":[2,3],\"choice\":\"Second\",\"note\":null}", true)]
    [InlineData("{\"inherited\":\"base\",\"display_name\":\"ok\",\"nested\":{\"value\":1},\"values\":[],\"note\":null,\"restricted\":null}", false)]
    public async Task ShouldAdvertiseTheEffectiveBindingContract(string input, bool valid)
    {
        var probe = new InvocationCount();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(probe);
        _ = builder.Services.AddPortia().AddRequestHandler<SchemaProbeHandler>().AddMcpTool<SchemaProbe>().AddMcpHttp();
        await using var app = builder.Build();
        app.Use((context, next) =>
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "schema-test-actor")], "test"));
            return next(context);
        });
        _ = app.MapPortiaMcp().AllowAnonymous();
        await app.StartAsync();
        await using var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri("http://localhost/mcp")
        }, app.GetTestClient());
        await using var client = await McpClient.CreateAsync(transport);
        var tool = Assert.Single(await client.ListToolsAsync());
        var schema = JsonSchema.Build(tool.JsonSchema,
            new BuildOptions { Dialect = Dialect.Draft202012, SchemaRegistry = new() });
        using var document = JsonDocument.Parse(input);
        var arguments = document.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => (object?)property.Value.Clone());
        var result = await client.CallToolAsync(tool.Name, arguments);
        Assert.True((result.IsError == true) == !valid,
            string.Join("; ", result.Content.OfType<TextContentBlock>().Select(content => content.Text)));
        Assert.Equal(valid ? 1 : 0, probe.Count);
        Assert.Equal(valid, schema.Evaluate(document.RootElement).IsValid);
        if (valid)
        {
            var output = Assert.IsType<JsonElement>(tool.ProtocolTool.OutputSchema);
            var outputSchema = JsonSchema.Build(output,
                new BuildOptions { Dialect = Dialect.Draft202012, SchemaRegistry = new() });
            Assert.True(outputSchema.Evaluate(Assert.IsType<JsonElement>(result.StructuredContent)).IsValid);
        }
    }

    [Fact]
    public async Task ShouldAdvertiseAndEnforceTheInputContractOverStdio()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var host = Path.GetFullPath(
            $"../../../../../smoke/Portia.McpStdioHost/bin/{configuration}/net10.0/Portia.McpStdioHost.dll",
            AppContext.BaseDirectory);
        await using var client = await McpClient.CreateAsync(new StdioClientTransport(
            new StdioClientTransportOptions
            {
                Command = "dotnet",
                Arguments = [host],
                Name = "MCP schema qualification",
                ShutdownTimeout = TimeSpan.FromSeconds(1)
            }));

        var tool = Assert.Single(await client.ListToolsAsync());
        var schema = JsonSchema.Build(tool.JsonSchema,
            new BuildOptions { Dialect = Dialect.Draft202012, SchemaRegistry = new() });
        using var missing = JsonDocument.Parse("{}");
        Assert.False(schema.Evaluate(missing.RootElement).IsValid);
        var rejected = await client.CallToolAsync(tool.Name, new Dictionary<string, object?>());
        Assert.True(rejected.IsError);

        var arguments = new Dictionary<string, object?> { ["name"] = "stdio" };
        using var valid = JsonDocument.Parse(JsonSerializer.Serialize(arguments));
        Assert.True(schema.Evaluate(valid.RootElement).IsValid);
        var accepted = await client.CallToolAsync(tool.Name, arguments);
        Assert.False(accepted.IsError);
        Assert.Contains("stdio", Assert.IsType<TextContentBlock>(Assert.Single(accepted.Content)).Text,
            StringComparison.Ordinal);
    }

    public sealed class InvocationCount
    {
        public int Count { get; set; }
    }

    public record SchemaBase
    {
        public string Inherited { get; init; } = "base";
    }

    /// <summary>Inspects required, nullable, defaulted, inherited and converter-backed arguments.</summary>
    [Discriminator("schema.probe")]
    public sealed record SchemaProbe(
        [property: JsonPropertyName("display_name")] string Name,
        SchemaNested Nested, int[] Values, string? Note, SchemaChoice Choice = SchemaChoice.First,
        int Count = 7, [param: DisallowNull] string? Restricted = "") : SchemaBase, IRequest<SchemaReply>, ICallable;

    public sealed record SchemaNested(int Value, string SourceRecordId = "source");
    public sealed record SchemaReply(string Name, SchemaNested Nested, string? Note, SchemaChoice Choice, int Count);

    [JsonConverter(typeof(JsonStringEnumConverter<SchemaChoice>))]
    public enum SchemaChoice { First, Second }

    /// <summary>Returns a successful nullable value.</summary>
    [Discriminator("schema.nullable")]
    public sealed record NullableReply : IRequest<int?>, ICallable;

    /// <summary>Completes without a result value.</summary>
    [Discriminator("schema.no-result")]
    public sealed record NoResult : IRequest, ICallable;

    public sealed class NullableReplyHandler : IRequestHandler<NullableReply, int?>
    {
        public ValueTask<Result<int?>> HandleAsync(IRequestContext<NullableReply> context, CancellationToken ct) =>
            ValueTask.FromResult(Result<int?>.Success(null));
    }

    public sealed class NoResultHandler : IRequestHandler<NoResult>
    {
        public ValueTask<Result> HandleAsync(IRequestContext<NoResult> context, CancellationToken ct) =>
            ValueTask.FromResult(Result.Success);
    }

    /// <summary>A converter-shaped request cannot be advertised as an object-shaped MCP tool input.</summary>
    [Discriminator("schema.scalar")]
    [JsonConverter(typeof(ScalarProbeConverter))]
    public sealed record ScalarProbe(string Value) : IRequest, ICallable;

    public sealed class ScalarProbeConverter : JsonConverter<ScalarProbe>
    {
        public override ScalarProbe Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            new(reader.GetString()!);
        public override void Write(Utf8JsonWriter writer, ScalarProbe value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }

    public sealed class ScalarProbeHandler : IRequestHandler<ScalarProbe>
    {
        public ValueTask<Result> HandleAsync(IRequestContext<ScalarProbe> context, CancellationToken ct) =>
            ValueTask.FromResult(Result.Success);
    }

    public sealed class SchemaProbeHandler(InvocationCount probe) : IRequestHandler<SchemaProbe, SchemaReply>
    {
        public ValueTask<Result<SchemaReply>> HandleAsync(IRequestContext<SchemaProbe> context, CancellationToken ct)
        {
            probe.Count++;
            var request = context.Request;
            return ValueTask.FromResult(Result<SchemaReply>.Success(new SchemaReply(request.Name, request.Nested,
                request.Note, request.Choice, request.Count)));
        }
    }
}

[PortiaJsonContext]
[JsonSerializable(typeof(McpSchemaFidelityTests.SchemaProbe))]
[JsonSerializable(typeof(McpSchemaFidelityTests.SchemaReply))]
[JsonSerializable(typeof(McpSchemaFidelityTests.ScalarProbe))]
[JsonSerializable(typeof(McpSchemaFidelityTests.NullableReply))]
[JsonSerializable(typeof(McpSchemaFidelityTests.NoResult))]
[JsonSerializable(typeof(int?))]
sealed partial class McpSchemaJsonContext : JsonSerializerContext;
