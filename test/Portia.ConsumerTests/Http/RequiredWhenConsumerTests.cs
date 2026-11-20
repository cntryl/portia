using System.Text.Json;

namespace Cntryl.Portia.Consumer;

public sealed class RequiredWhenConsumerTests
{
    [Theory]
    [InlineData("""{"plan":"scheduled"}""", 400)]
    [InlineData("""{"plan":"scheduled","send_at":"  "}""", 400)]
    [InlineData("""{"plan":"scheduled","send_at":"later"}""", 200)]
    [InlineData("""{"plan":"SCHEDULED"}""", 200)]
    [InlineData("""{"plan":"manual"}""", 200)]
    public async Task ShouldRequireValueGivenMatchingOrdinalSelector(string json, int expected)
    {
        // Arrange
        var assembly = Compile("app.MapPortiaPost<Binding, string>(\"/jobs\");");

        // Act
        var (status, _) = await HttpConsumerScenario.RunAsync(assembly, "/jobs", json);

        // Assert
        Assert.Equal(expected, status);
    }

    [Fact]
    public async Task ShouldEmitConditionGivenRenamedJsonMembers()
    {
        // Arrange
        var assembly = Compile("app.MapPortiaPost<Binding, string>(\"/jobs\"); app.MapPortiaOpenApi();");

        // Act
        var (status, json) = await HttpConsumerScenario.RunAsync(assembly, "/openapi/v1.json");

        // Assert
        Assert.Equal(200, status);
        using var document = JsonDocument.Parse(json);
        var schema = document.RootElement.GetProperty("paths").GetProperty("/jobs").GetProperty("post")
            .GetProperty("requestBody").GetProperty("content").GetProperty("application/json").GetProperty("schema");
        var condition = schema.GetProperty("allOf")[0];
        Assert.Equal("plan", condition.GetProperty("if").GetProperty("required")[0].GetString());
        Assert.Equal("scheduled", condition.GetProperty("if").GetProperty("properties")
            .GetProperty("plan").GetProperty("const").GetString());
        Assert.Equal("send_at", condition.GetProperty("then").GetProperty("required")[0].GetString());
        Assert.Equal("string", condition.GetProperty("then").GetProperty("properties")
            .GetProperty("send_at").GetProperty("type").GetString());
    }

    [Theory]
    [InlineData("endpoint => endpoint.FromQuery(x => x.Mode)")]
    [InlineData("endpoint => endpoint.FromHeader(x => x.SendAt, \"X-Send-At\")")]
    [InlineData("endpoint => endpoint.Accepts<Binding>(\"application/json\").OnBind(_ => new Binding(\"scheduled\", \"now\"))")]
    public async Task ShouldRejectEndpointMappingGivenConditionalMemberSourceOverride(string configure)
    {
        // Arrange
        var assembly = Compile($"app.MapPortiaPost<Binding, string>(\"/jobs\", {configure});");

        // Act
        var error = await Record.ExceptionAsync(() =>
            HttpConsumerScenario.RunAsync(assembly, "/jobs", """{"plan":"scheduled"}"""));

        // Assert
        Assert.Contains("RequiredWhen", Assert.IsType<InvalidOperationException>(error).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShouldRejectMappingGivenSelectorConverterWithDifferentSemantics()
    {
        // Arrange
        var assembly = HttpConsumerScenario.Compile(
            """
            public sealed record Binding(
                [property: JsonConverter(typeof(LowerCaseMode))] string Mode,
                [RequiredWhen(nameof(Binding.Mode), "scheduled")] string? SendAt = null)
                : IRequest<string>, ICallable;
            public sealed class LowerCaseMode : JsonConverter<string>
            {
                public override string Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
                    => reader.GetString()!.ToLowerInvariant();
                public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
                    => writer.WriteStringValue(value);
            }
            """,
            "request.Mode",
            "app.MapPortiaPost<Binding, string>(\"/jobs\");");

        // Act
        var error = await Record.ExceptionAsync(() =>
            HttpConsumerScenario.RunAsync(assembly, "/jobs", """{"mode":"SCHEDULED"}"""));

        // Assert
        Assert.Contains("RequiredWhen", Assert.IsType<InvalidOperationException>(error).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShouldAuthorizeFirstGivenUnmetConditionalRequirement()
    {
        // Arrange
        var assembly = HttpConsumerScenario.Compile(
            """
            [RequiresPermission("jobs:schedule")]
            public sealed record Binding(string Mode,
                [RequiredWhen(nameof(Binding.Mode), "scheduled")] string? SendAt = null)
                : IRequest<string>, ICallable;
            public sealed class Permissions : IPermissionEvaluator
            {
                public ValueTask<Result> EvaluateAsync(System.Security.Claims.ClaimsPrincipal actor, string permission,
                    CancellationToken ct = default) => ValueTask.FromResult(actor.Identity?.IsAuthenticated == true
                        ? Result.Success
                        : Result.Failure(RequestError.Unauthorized("Authentication required.")));
            }
            """,
            "request.Mode",
            "app.MapPortiaPost<Binding, string>(\"/jobs\");",
            "builder.Services.AddSingleton<IPermissionEvaluator, Permissions>();");

        // Act
        var (status, _) = await HttpConsumerScenario.RunAsync(assembly, "/jobs", """{"mode":"scheduled"}""");

        // Assert
        Assert.Equal(401, status);
    }

    static System.Reflection.Assembly Compile(string mapping) => HttpConsumerScenario.Compile(
        """
        public sealed record Binding(
            [property: JsonPropertyName("plan")] string Mode,
            [property: JsonPropertyName("send_at"), RequiredWhen(nameof(Binding.Mode), "scheduled")] string? SendAt = null)
            : IRequest<string>, ICallable;
        """,
        "request.SendAt ?? request.Mode",
        mapping);
}
