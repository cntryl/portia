using System.Text.Json;

namespace Cntryl.Portia.Consumer;

public sealed class DecimalStringConsumerTests
{
    [Theory]
    [InlineData("/quotes/1.25?maximum=2.125", 200)]
    [InlineData("/quotes/1.25?maximum=1.25", 200)]
    [InlineData("/quotes/1?maximum=1", 400)]
    [InlineData("/quotes/1.00000000000000000000000000001?maximum=2", 400)]
    public async Task ShouldApplyExactContractGivenRouteAndQueryValues(string path, int expected)
    {
        // Arrange
        var assembly = HttpConsumerScenario.Compile(
            """
            public sealed record Binding(DecimalString Amount,
                [property: DecimalStringRange("1", "2.125", MinimumIsExclusive = true)] DecimalString? Maximum = null)
                : IRequest<DecimalString>, ICallable;
            """,
            "request.Amount",
            "app.MapPortiaGet<Binding, DecimalString>(\"/quotes/{amount}\");",
            resultType: "DecimalString");

        // Act
        var (status, body) = await HttpConsumerScenario.RunAsync(assembly, path);

        // Assert
        Assert.Equal(expected, status);

        if (expected == 200)
            Assert.Equal("\"1.25\"", body);
    }

    [Fact]
    public async Task ShouldDescribeExactStringAndRangeGivenAnnotatedJsonMember()
    {
        // Arrange
        var assembly = HttpConsumerScenario.Compile(
            """
            public sealed record Binding(
                [property: DecimalStringRange("1.0000000000000000000000000001", "9", MaximumIsExclusive = true)]
                DecimalString? Amount = null) : IRequest<string>, ICallable;
            """,
            "request.Amount?.ToString() ?? \"none\"",
            "app.MapPortiaPost<Binding, string>(\"/quotes\"); app.MapPortiaOpenApi();");

        // Act
        var (status, document) = await HttpConsumerScenario.RunAsync(assembly, "/openapi/v1.json");
        var (numericStatus, _) = await HttpConsumerScenario.RunAsync(assembly, "/quotes", """{"amount":1.25}""");
        var (validStatus, body) = await HttpConsumerScenario.RunAsync(assembly, "/quotes", """{"amount":"1.25"}""");

        // Assert
        Assert.Equal(200, status);
        Assert.Equal(400, numericStatus);
        Assert.Equal(200, validStatus);
        Assert.Equal("\"1.25\"", body);
        using var openApi = JsonDocument.Parse(document);
        var root = openApi.RootElement;
        var schema = root.GetProperty("paths").GetProperty("/quotes").GetProperty("post")
            .GetProperty("requestBody").GetProperty("content").GetProperty("application/json").GetProperty("schema");

        if (schema.TryGetProperty("$ref", out var reference))
            schema = root.GetProperty("components").GetProperty("schemas")
                .GetProperty(reference.GetString()!.Split('/')[^1]);

        var amount = schema.GetProperty("properties").GetProperty("amount");
        Assert.Equal(["string", "null"], amount.GetProperty("type").EnumerateArray().Select(type => type.GetString()).OrderDescending(StringComparer.Ordinal));
        Assert.NotEmpty(amount.GetProperty("pattern").GetString()!);
        var range = amount.GetProperty("x-portia-range");
        Assert.Equal("1.0000000000000000000000000001", range.GetProperty("minimum").GetString());
        Assert.True(range.GetProperty("maximumIsExclusive").GetBoolean());
    }
}
