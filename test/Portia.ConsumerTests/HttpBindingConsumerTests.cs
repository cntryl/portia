namespace Cntryl.Portia.Consumer;

public sealed class HttpBindingConsumerTests
{
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
    public async Task QueryBindingPreservesNullableDefaultAndRequiredValues(string query, int expectedStatus, string? expected)
    {
        var assembly = HttpConsumerScenario.Compile(
            "public sealed record Binding(string Required, int Limit = 7, int? Optional = null, string? Note = null) : IRequest<string>, ICallable;",
            "request.Required + \"/\" + request.Limit + \"/\" + (request.Optional?.ToString() ?? \"null\") + \"/\" + (request.Note ?? \"null\")",
            "app.MapPortiaGet<Binding, string>(\"/binding\");");
        var (status, body) = await HttpConsumerScenario.RunAsync(assembly, "/binding" + query);
        Assert.Equal(expectedStatus, status);
        if (expected is not null)
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(expected), body);
    }

    [Theory]
    [InlineData(false, false, /*lang=json,strict*/ "{\"displayName\":\"hello\",\"note\":null,\"memo\":\"m\"}", 200, "20/hello/7/null/m")]
    [InlineData(true, false, /*lang=json,strict*/ "{\"display_name\":\"hello\"}", 200, "20/hello/7/null/null")]
    [InlineData(false, true, /*lang=json,strict*/ "{\"displayName\":\"hello\",\"quantity\":\"0x10\"}", 200, "20/hello/16/null/null")]
    [InlineData(false, false, /*lang=json,strict*/ "{\"displayName\":42}", 400, null)]
    [InlineData(false, false, /*lang=json,strict*/ "{\"displayName\":null}", 400, null)]
    [InlineData(false, false, /*lang=json,strict*/ "{\"displayName\":\"hello\",\"quantity\":null}", 400, null)]
    [InlineData(false, false, "{}", 400, null)]
    [InlineData(false, false, "[]", 400, null)]
    [InlineData(false, false, "null", 400, null)]
    public async Task JsonBindingHonorsOptionsNamesConvertersNullsAndKinds(bool snake, bool converter, string json, int expectedStatus, string? expected)
    {
        var assembly = HttpConsumerScenario.Compile(
            "public sealed record Binding(int Id, string DisplayName, int Quantity = 7, string? Note = null, [property: JsonPropertyName(\"memo\")] string? Comment = null) : IRequest<string>, ICallable;",
            "request.Id + \"/\" + request.DisplayName + \"/\" + request.Quantity + \"/\" + (request.Note ?? \"null\") + \"/\" + (request.Comment ?? \"null\")",
            "app.MapPortiaPost<Binding, string>(\"/binding/{id}\");");
        var (status, body) = await HttpConsumerScenario.RunAsync(assembly, "/binding/20", json, snake, converter);
        Assert.Equal(expectedStatus, status);
        if (expected is not null)
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(expected), body);
    }
}
