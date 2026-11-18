using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

namespace Cntryl.Portia;

/// <summary>OpenAPI contracts must match the configured serializer, binder and authorization runtime.</summary>
public sealed class OpenApiContractFidelityTests
{
    /// <summary>Preserves the runtime contract in the published schema.</summary>
    [Fact]
    public async Task ShouldOmitQueryDefaultGivenRejectedBindingDefault()
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        _ = builder.Services.AddPortia().AddHttp().AddRequestHandler<AuditHandler>();
        await using var app = builder.Build();
        _ = app.MapPortiaOpenApi();
        _ = app.MapPortiaGet<InvalidDefaultRequest, string>("/query-default");
        await app.StartAsync();
        using var omitted = await app.GetTestClient().GetAsync("/query-default");
        using var valid = await app.GetTestClient().GetAsync("/query-default?limit=1");

        // Act
        var document = OpenApiDocument.Parse(await app.GetTestClient().GetStringAsync("/openapi/v1.json")).Document!;

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, omitted.StatusCode);
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        var parameter =
            Assert.Single(document.Paths["/query-default"].Operations![System.Net.Http.HttpMethod.Get].Parameters!);
        Assert.True(parameter.Required);
        Assert.Null(parameter.Schema!.Default);
    }

    /// <summary>Preserves the runtime contract in the published schema.</summary>
    [Fact]
    public async Task ShouldUsePortiaSerializerGivenExplicitResponse()
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        _ = builder.Services.AddPortia()
            .ConfigureJson(options => options.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower)
            .AddHttp().AddRequestHandler<AuditHandler>();
        await using var app = builder.Build();
        _ = app.MapPortiaOpenApi();
        _ = app.MapPortiaGet<ReadAudit, AuditReply>("/explicit-response", config => config.Produces<AuditReply>(201)
            .OnResult((http, result) => Results.Json(result.Value,
                http.RequestServices.GetRequiredKeyedService<JsonSerializerOptions>(PortiaServiceKeys.Json),
                statusCode: 201)));
        await app.StartAsync();
        var actual = JsonNode.Parse(await app.GetTestClient().GetStringAsync("/explicit-response"))!;

        // Act
        var document = OpenApiDocument.Parse(await app.GetTestClient().GetStringAsync("/openapi/v1.json")).Document!;

        // Assert
        Assert.Equal("accepted", actual["long_property_name"]!.GetValue<string>());
        var schema =
            document.Paths["/explicit-response"].Operations![System.Net.Http.HttpMethod.Get].Responses!["201"].Content![
                "application/json"].Schema!;
        Assert.Contains("long_property_name", schema.Properties!.Keys);
        Assert.DoesNotContain("longPropertyName", schema.Properties.Keys);
        Assert.Equal(20, schema.Properties["long_property_name"].MaxLength);
    }

    /// <summary>Preserves the runtime contract in the published schema.</summary>
    [Fact]
    public async Task ShouldDescribeCredentialAlternativesGivenCombinedPolicies()
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        _ = builder.Services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, AuditAuthHandler>("alpha", _ => { })
            .AddScheme<AuthenticationSchemeOptions, AuditAuthHandler>("beta", _ => { });
        _ = builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy("alpha-policy",
                policy => policy.AddAuthenticationSchemes("alpha").RequireAuthenticatedUser());
            options.AddPolicy("beta-policy",
                policy => policy.AddAuthenticationSchemes("beta").RequireAuthenticatedUser());
        });
        _ = builder.Services.AddPortia().AddHttp(options =>
        {
            options.OpenApi.SecuritySchemes.Add("alpha",
                new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "bearer" });
            options.OpenApi.SecuritySchemes.Add("beta",
                new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "bearer" });
            _ = options.OpenApi.SecurityPolicy("alpha-policy", "alpha");
            _ = options.OpenApi.SecurityPolicy("beta-policy", "beta");
        }).AddRequestHandler<AuditHandler>();
        await using var app = builder.Build();
        _ = app.MapGroup("").AllowAnonymous().MapPortiaOpenApi();
        _ = app.MapPortiaGet<ReadAudit, AuditReply>("/secured").RequireAuthorization("alpha-policy", "beta-policy");
        await app.StartAsync();
        var client = app.GetTestClient();
        using var anonymous = await client.GetAsync("/secured");
        _ = client.DefaultRequestHeaders.TryAddWithoutValidation("X-Alpha", "yes");
        using var alphaOnly = await client.GetAsync("/secured");
        _ = client.DefaultRequestHeaders.Remove("X-Alpha");
        _ = client.DefaultRequestHeaders.TryAddWithoutValidation("X-Beta", "yes");
        using var betaOnly = await client.GetAsync("/secured");

        // Act
        var document = JsonNode.Parse(await client.GetStringAsync("/openapi/v1.json"))!;

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.OK, alphaOnly.StatusCode);
        Assert.Equal(HttpStatusCode.OK, betaOnly.StatusCode);
        var security = document["paths"]!["/secured"]!["get"]!["security"]!.AsArray();
        Assert.Equal(2, security.Count);
        Assert.All(security, item => Assert.Single(item!.AsObject()));
    }

    /// <summary>Preserves the runtime contract in the published schema.</summary>
    [Fact]
    public async Task ShouldKeepSchemaExamplesSeparateGivenDirectionalRegistrations()
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        _ = builder.Services.AddPortia().AddExample("input", new AuditReply("input"), use: PortiaContractUse.Input)
            .AddExample("output", new AuditReply("output"), use: PortiaContractUse.Output)
            .AddHttp().AddRequestHandler<AuditHandler>();
        await using var app = builder.Build();
        _ = app.MapPortiaOpenApi();
        _ = app.MapPortiaPost<RoundTripAudit, AuditReply>("/round-trip");
        await app.StartAsync();

        // Act
        var document = OpenApiDocument.Parse(await app.GetTestClient().GetStringAsync("/openapi/v1.json")).Document!;

        // Assert
        var operation = document.Paths["/round-trip"].Operations![System.Net.Http.HttpMethod.Post];
        var input = operation.RequestBody!.Content!["application/json"].Schema!.Properties!["payload"];
        var output = operation.Responses!["200"].Content!["application/json"].Schema!;
        Assert.Equal("input", input.Examples![0]!["long_property_name"]!.GetValue<string>());
        Assert.Equal("output", output.Examples![0]!["long_property_name"]!.GetValue<string>());
    }

    /// <summary>Preserves the runtime contract in the published schema.</summary>
    [Fact]
    public async Task ShouldRequireBooleanInFormGivenRejectedOmission()
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        _ = builder.Services.AddPortia().AddHttp().AddRequestHandler<AuditHandler>();
        await using var app = builder.Build();
        _ = app.MapPortiaOpenApi();
        _ = app.MapPortiaPost<ConfirmAudit, string>("/confirm").DisableAntiforgery();
        await app.StartAsync();
        using var missing = await app.GetTestClient().PostAsync("/confirm", new System.Net.Http.FormUrlEncodedContent(
            new System.Collections.Generic.Dictionary<string, string> { ["marker"] = "present" }));
        using var valid = await app.GetTestClient().PostAsync("/confirm", new System.Net.Http.FormUrlEncodedContent(
            new System.Collections.Generic.Dictionary<string, string>
            { ["accepted"] = "true", ["marker"] = "present" }));

        // Act
        var document = OpenApiDocument.Parse(await app.GetTestClient().GetStringAsync("/openapi/v1.json")).Document!;

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        var schema = document.Paths["/confirm"].Operations![System.Net.Http.HttpMethod.Post].RequestBody!
            .Content!["application/x-www-form-urlencoded"].Schema!;
        Assert.Contains("accepted", schema.Required?.ToArray() ?? []);
        Assert.DoesNotContain("ordinary", schema.Required?.ToArray() ?? []);
    }

    /// <summary>Preserves the runtime contract in the published schema.</summary>
    [Fact]
    public void ShouldOmitPortablePatternGivenClosingBracketClass()
    {
        // Arrange
        var rule = new RegularExpressionAttribute("[]]");
        var member = new PortiaContractMemberMetadata("Value", typeof(string)) { Rules = [rule] };
        var schema = new JsonObject { ["type"] = "string" };

        // Act
        PortiaJsonConstraints.Apply(schema, member, JsonSerializerOptions.Web);

        // Assert
        Assert.True(rule.IsValid("]"));
        Assert.Null(schema["pattern"]);
        Assert.Equal("[]]", schema["x-portia-dotnet-pattern"]!.GetValue<string>());
    }

    /// <summary>Literal .NET braces outside a quantifier are syntax errors in Unicode ECMAScript.</summary>
    [Theory]
    [InlineData("a{")]
    [InlineData("a}")]
    [InlineData("a{b}")]
    public void ShouldOmitPortablePatternGivenLiteralBraces(string pattern)
    {
        // Arrange
        var rule = new RegularExpressionAttribute(pattern);
        var member = new PortiaContractMemberMetadata("Value", typeof(string)) { Rules = [rule] };
        var schema = new JsonObject { ["type"] = "string" };

        // Act
        PortiaJsonConstraints.Apply(schema, member, JsonSerializerOptions.Web);

        // Assert
        Assert.True(rule.IsValid(pattern));
        Assert.Null(schema["pattern"]);
        Assert.Equal(pattern, schema["x-portia-dotnet-pattern"]!.GetValue<string>());
    }

    /// <summary>The schema must use the first match chosen by the runtime before checking its length.</summary>
    [Theory]
    [InlineData("a|ab", "ab", false)]
    [InlineData("a+?", "aa", false)]
    [InlineData("(ab|a)b?", "abb", true)]
    [InlineData("a|ab", "", true)]
    public void ShouldMatchRuntimeGivenCompetingRegexMatches(string pattern, string input, bool expected)
    {
        // Arrange
        var rule = new RegularExpressionAttribute(pattern);
        var member = new PortiaContractMemberMetadata("Value", typeof(string)) { Rules = [rule] };
        var schema = new JsonObject { ["type"] = "string" };

        // Act
        PortiaJsonConstraints.Apply(schema, member, JsonSerializerOptions.Web);

        // Assert
        Assert.Equal(expected, rule.IsValid(input));
        Assert.Equal(expected, Regex.IsMatch(input, schema["pattern"]!.GetValue<string>(),
            RegexOptions.ECMAScript));
    }

    /// <summary>Ordinary ASCII classes, groups and quantifiers keep their portable constraints.</summary>
    [Theory]
    [InlineData("[A-Z]{2}")]
    [InlineData("(ab|cd)+[0-9]?")]
    public void ShouldRetainPortablePatternGivenUnambiguousAsciiSyntax(string pattern)
    {
        // Arrange
        var member = new PortiaContractMemberMetadata("Value", typeof(string))
        { Rules = [new RegularExpressionAttribute(pattern)] };
        var schema = new JsonObject { ["type"] = "string" };

        // Act
        PortiaJsonConstraints.Apply(schema, member, JsonSerializerOptions.Web);

        // Assert
        Assert.NotNull(schema["pattern"]);
        Assert.Equal(pattern, schema["x-portia-dotnet-pattern"]!.GetValue<string>());
    }

    /// <summary>Preserves the runtime contract in the published schema.</summary>
    [Fact]
    public void ShouldOmitLossyRangeBoundsGivenInt64Enum()
    {
        // Arrange
        var range = new RangeAttribute(0D, 9_007_199_254_740_992D);
        var member = new PortiaContractMemberMetadata("Value", typeof(AuditLargeMode)) { Rules = [range] };
        var schema = new JsonObject { ["type"] = "integer" };

        // Act
        PortiaJsonConstraints.Apply(schema, member, JsonSerializerOptions.Web);

        // Assert
        Assert.True(range.IsValid(AuditLargeMode.Rounded));
        Assert.True(Convert.ToDecimal(AuditLargeMode.Rounded, CultureInfo.InvariantCulture) > 9_007_199_254_740_992m);
        Assert.Null(schema["maximum"]);
    }
}
