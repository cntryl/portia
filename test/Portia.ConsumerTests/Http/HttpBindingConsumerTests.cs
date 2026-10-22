using System.Text.Json;

namespace Cntryl.Portia.Consumer;

public sealed class HttpBindingConsumerTests
{
    [Theory]
    [InlineData("/binding", 404)]
    [InlineData("/binding/malformed", 400)]
    [InlineData("/binding/00000000-0000-0000-0000-000000000000", 400)]
    [InlineData("/binding/11111111-1111-4111-8111-111111111111", 200)]
    public async Task ShouldValidateRequiredTypedIdentityGivenHttpRoute(string path, int expectedStatus)
    {
        // Arrange
        var assembly = HttpConsumerScenario.Compile(
            "public sealed record Binding(Uuid CommandId, string Value) : IRequest<string>, ICallable, IIdentifiedRequest { Uuid IIdentifiedRequest.RequestId => CommandId; }",
            "context.RequestId.ToString()",
            "app.MapPortiaPost<Binding, string>(\"/binding/{command_id}\");");

        // Act
        var (status, _) = await HttpConsumerScenario.RunAsync(assembly, path, "{\"value\":\"ok\"}");

        // Assert
        Assert.Equal(expectedStatus, status);
    }

    [Fact]
    public async Task ShouldUseTypedLogicalIdentityGivenRoutedCommand()
    {
        // Arrange
        var id = Uuid.CreateVersion4();
        var assembly = HttpConsumerScenario.Compile(
            "public sealed record Binding(Uuid CommandId, string Value) : IRequest<string>, ICallable, IIdentifiedRequest { Uuid IIdentifiedRequest.RequestId => CommandId; }",
            "context.RequestId.ToString()",
            "app.MapPortiaPost<Binding, string>(\"/binding/{command_id}\");");

        // Act
        var (status, body) = await HttpConsumerScenario.RunAsync(assembly, "/binding/" + id, "{\"value\":\"ok\"}");

        // Assert
        Assert.Equal(200, status);
        Assert.Equal(JsonSerializer.Serialize(id.ToString()), body);
    }

    [Theory]
    [InlineData("{\"value\":\"ok\"}", 200)]
    [InlineData("{\"commandId\":7,\"value\":\"ok\"}", 200)]
    [InlineData("{\"commandId\":8,\"value\":\"ok\"}", 400)]
    [InlineData("{\"commandId\":null,\"value\":\"ok\"}", 400)]
    [InlineData("{\"commandId\":\"bad\",\"value\":\"ok\"}", 400)]
    [InlineData("{\"commandId\":7,\"CommandId\":8,\"value\":\"ok\"}", 400)]
    [InlineData("{\"commandId\":8,\"CommandId\":7,\"value\":\"ok\"}", 400)]
    [InlineData("{\"commandId\":7,\"commandId\":7,\"value\":\"ok\"}", 200)]
    public async Task ShouldRejectConflictingBodyDuplicateGivenRequiredRouteValue(string json, int expectedStatus)
    {
        // Arrange
        var assembly = HttpConsumerScenario.Compile(
            "public sealed record Binding(int CommandId, string Value) : IRequest<string>, ICallable;",
            "request.CommandId.ToString(CultureInfo.InvariantCulture) + request.Value",
            "app.MapPortiaPost<Binding, string>(\"/binding/{command_id}\");");

        // Act
        var (status, _) = await HttpConsumerScenario.RunAsync(assembly, "/binding/7", json);

        // Assert
        Assert.Equal(expectedStatus, status);
    }

    [Fact]
    public async Task LiteralRouteBaselineCompilesAndExecutes()
    {
        var assembly = HttpConsumerScenario.Compile(
            "public sealed record Binding(int Id) : IRequest<string>, ICallable;",
            "request.Id.ToString(CultureInfo.InvariantCulture)",
            "app.MapPortiaGet<Binding, string>(\"/binding/{id}\");");
        var (status, body) = await HttpConsumerScenario.RunAsync(assembly, "/binding/7");
        Assert.Equal(200, status);
        Assert.Equal("\"7\"", body);
    }

    [Fact]
    public async Task ConstantRouteBindsIdenticallyToLiteral()
    {
        var assembly = HttpConsumerScenario.Compile(
            "public sealed record Binding(int Id) : IRequest<string>, ICallable;",
            "request.Id.ToString(CultureInfo.InvariantCulture)",
            "const string Route = \"/binding/{id}\"; app.MapPortiaGet<Binding, string>(Route);");
        var (status, body) = await HttpConsumerScenario.RunAsync(assembly, "/binding/7");
        Assert.Equal(200, status);
        Assert.Equal("\"7\"", body);
    }

    [Theory]
    [InlineData("?required=x", 200, "x/7/null/null")]
    [InlineData("?required=x&limit=9&optional=2&note=", 200, "x/9/2/")]
    [InlineData("", 400, null)]
    [InlineData("?required=x&optional=bad", 400, null)]
    public async Task QueryBindingPreservesNullableDefaultAndRequiredValues(string query, int expectedStatus,
        string? expected)
    {
        var assembly = HttpConsumerScenario.Compile(
            "public sealed record Binding(string Required, int Limit = 7, int? Optional = null, string? Note = null) : IRequest<string>, ICallable;",
            "request.Required + \"/\" + request.Limit + \"/\" + (request.Optional?.ToString() ?? \"null\") + \"/\" + (request.Note ?? \"null\")",
            "app.MapPortiaGet<Binding, string>(\"/binding\");");
        var (status, body) = await HttpConsumerScenario.RunAsync(assembly, "/binding" + query);
        Assert.Equal(expectedStatus, status);
        if (expected is not null)
        {
            Assert.Equal(JsonSerializer.Serialize(expected), body);
        }
    }

    [Theory]
    [InlineData(false, false, /*lang=json,strict*/ "{\"displayName\":\"hello\",\"note\":null,\"memo\":\"m\"}", 200,
        "20/hello/7/null/m")]
    [InlineData(true, false, /*lang=json,strict*/ "{\"display_name\":\"hello\"}", 200, "20/hello/7/null/null")]
    [InlineData(false, true, /*lang=json,strict*/ "{\"displayName\":\"hello\",\"quantity\":\"0x10\"}", 200,
        "20/hello/16/null/null")]
    [InlineData(false, false, /*lang=json,strict*/ "{\"displayName\":42}", 400, null)]
    [InlineData(false, false, /*lang=json,strict*/ "{\"displayName\":null}", 400, null)]
    [InlineData(false, false, /*lang=json,strict*/ "{\"displayName\":\"hello\",\"quantity\":null}", 400, null)]
    [InlineData(false, false, "{}", 400, null)]
    [InlineData(false, false, "[]", 400, null)]
    [InlineData(false, false, "null", 400, null)]
    public async Task JsonBindingHonorsOptionsNamesConvertersNullsAndKinds(bool snake, bool converter, string json,
        int expectedStatus, string? expected)
    {
        var assembly = HttpConsumerScenario.Compile(
            "public sealed record Binding(int Id, string DisplayName, int Quantity = 7, string? Note = null, [property: JsonPropertyName(\"memo\")] string? Comment = null) : IRequest<string>, ICallable;",
            "request.Id + \"/\" + request.DisplayName + \"/\" + request.Quantity + \"/\" + (request.Note ?? \"null\") + \"/\" + (request.Comment ?? \"null\")",
            "app.MapPortiaPost<Binding, string>(\"/binding/{id}\");");
        var (status, body) = await HttpConsumerScenario.RunAsync(assembly, "/binding/20", json, snake, converter);
        Assert.Equal(expectedStatus, status);
        if (expected is not null)
        {
            Assert.Equal(JsonSerializer.Serialize(expected), body);
        }
    }
}
