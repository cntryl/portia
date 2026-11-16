using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

namespace Cntryl.Portia;

/// <summary>HTTP credentials are documented from actual authorization metadata and explicit host mappings.</summary>
public sealed class OpenApiSecurityTests
{
    /// <summary>Publishes the explicitly mapped credential contract.</summary>
    [Fact]
    public async Task ShouldCombineAlternativesGivenIndependentPolicies()
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        _ = builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy("either", policy => policy.RequireAuthenticatedUser());
            options.AddPolicy("extra", policy => policy.RequireAuthenticatedUser());
        });
        _ = builder.Services.AddPortia().AddHttp(options =>
        {
            foreach (var name in (string[])["a", "b", "c"])
                options.OpenApi.SecuritySchemes.Add(name, new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "bearer" });
            _ = options.OpenApi.SecurityPolicy("either", new Dictionary<string, string[]> { ["a"] = [] },
                new Dictionary<string, string[]> { ["b"] = [] });
            _ = options.OpenApi.SecurityPolicy("extra", "c");
        }).AddRequestHandler<ContractMetadataHandler>();
        await using var app = builder.Build();
        _ = app.MapGroup("").AllowAnonymous().MapPortiaOpenApi();
        _ = app.MapPortiaGet<ContractMetadataRequest, ContractMetadataModel>("/secured").RequireAuthorization("either", "extra");
        await app.StartAsync();

        // Act
        var document = JsonNode.Parse(await app.GetTestClient().GetStringAsync("/openapi/v1.json"))!;

        // Assert
        var security = document["paths"]!["/secured"]!["get"]!["security"]!.AsArray();
        Assert.Equal(2, security.Count);
        Assert.All(security, item => Assert.Equal(2, item!.AsObject().Count));
        Assert.Contains(security, item => item!["a"] is not null && item["c"] is not null);
        Assert.Contains(security, item => item!["b"] is not null && item["c"] is not null);
        Assert.Empty(security[0]!["c"]!.AsArray());
        Assert.Equal(3, document["components"]!["securitySchemes"]!.AsObject().Count);
    }

    /// <summary>Resolves the actual default authentication scheme.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldDescribeDefaultSchemeGivenDefaultOrFallbackPolicy(bool fallback)
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        _ = builder.Services.AddAuthentication("session").AddCookie("session");
        _ = builder.Services.AddAuthorization(options =>
        {
            if (fallback)
                options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
        });
        _ = builder.Services.AddPortia().AddHttp(options => options.OpenApi.SecuritySchemes.Add("session",
            new OpenApiSecurityScheme { Type = SecuritySchemeType.ApiKey, In = ParameterLocation.Cookie, Name = "session" }))
            .AddRequestHandler<ContractMetadataHandler>();
        await using var app = builder.Build();
        _ = app.MapGroup("").AllowAnonymous().MapPortiaOpenApi();
        var endpoint = app.MapPortiaGet<ContractMetadataRequest, ContractMetadataModel>("/default-policy");
        if (!fallback)
            _ = endpoint.RequireAuthorization();
        await app.StartAsync();

        // Act
        var document = JsonNode.Parse(await app.GetTestClient().GetStringAsync("/openapi/v1.json"))!;

        // Assert
        var security = Assert.Single(document["paths"]!["/default-policy"]!["get"]!["security"]!.AsArray());
        Assert.Empty(security!["session"]!.AsArray());
    }

    /// <summary>Publishes the explicitly mapped credential contract.</summary>
    [Fact]
    public async Task ShouldRetainCoreMappingGivenAspAnonymousEndpoint()
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        _ = builder.Services.AddAuthorization();
        _ = builder.Services.AddPortia().AddHttp(options =>
        {
            options.OpenApi.SecuritySchemes.Add("api", new OpenApiSecurityScheme { Type = SecuritySchemeType.ApiKey, In = ParameterLocation.Header, Name = "X-Key" });
            _ = options.OpenApi.SecurityFor<ContractMetadataRequest>("api");
        }).AddRequestHandler<ContractMetadataHandler>();
        await using var app = builder.Build();
        _ = app.MapPortiaOpenApi();
        _ = app.MapPortiaGet<ContractMetadataRequest, ContractMetadataModel>("/core").RequireAuthorization().AllowAnonymous();
        await app.StartAsync();

        // Act
        var document = JsonNode.Parse(await app.GetTestClient().GetStringAsync("/openapi/v1.json"))!;

        // Assert
        var security = Assert.Single(document["paths"]!["/core"]!["get"]!["security"]!.AsArray());
        Assert.Empty(security!["api"]!.AsArray());
    }

    /// <summary>Publishes the explicitly mapped credential contract.</summary>
    [Fact]
    public async Task ShouldRejectDocumentationGivenUnmappedNamedPolicy()
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        _ = builder.Services.AddAuthorization(options => options.AddPolicy("missing", policy => policy.RequireAuthenticatedUser()));
        _ = builder.Services.AddPortia().AddHttp().AddRequestHandler<ContractMetadataHandler>();
        await using var app = builder.Build();
        _ = app.MapGroup("").AllowAnonymous().MapPortiaOpenApi();
        _ = app.MapPortiaGet<ContractMetadataRequest, ContractMetadataModel>("/missing").RequireAuthorization("missing");
        await app.StartAsync();

        // Act
        var exception = await Record.ExceptionAsync(() => app.GetTestClient().GetStringAsync("/openapi/v1.json"));

        // Assert
        var failure = Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("SecurityPolicy", failure.Message, StringComparison.Ordinal);
        Assert.Contains("missing", failure.Message, StringComparison.Ordinal);
    }
    /// <summary>An explicitly anonymous alternative stays optional alongside a credential alternative.</summary>
    [Fact]
    public async Task ShouldRetainAnonymousAlternativeGivenOptionalCredentialMapping()
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        _ = builder.Services.AddAuthorization(options => options.AddPolicy("optional", policy => policy.RequireAssertion(_ => true)));
        _ = builder.Services.AddPortia().AddHttp(options =>
        {
            options.OpenApi.SecuritySchemes.Add("api", new OpenApiSecurityScheme { Type = SecuritySchemeType.ApiKey, In = ParameterLocation.Header, Name = "X-Key" });
            _ = options.OpenApi.SecurityPolicy("optional", new Dictionary<string, string[]>(),
                new Dictionary<string, string[]> { ["api"] = [] });
        }).AddRequestHandler<ContractMetadataHandler>();
        await using var app = builder.Build();
        _ = app.MapGroup("").AllowAnonymous().MapPortiaOpenApi();
        _ = app.MapPortiaGet<ContractMetadataRequest, ContractMetadataModel>("/optional").RequireAuthorization("optional");
        await app.StartAsync();

        // Act
        var document = JsonNode.Parse(await app.GetTestClient().GetStringAsync("/openapi/v1.json"))!;

        // Assert
        var security = document["paths"]!["/optional"]!["get"]!["security"]!.AsArray();
        Assert.Equal(2, security.Count);
        Assert.Contains(security, item => item!.AsObject().Count == 0);
        Assert.Contains(security, item => item!["api"] is not null);
    }

    /// <summary>A named mapping must account for schemes explicitly attached to the same authorization data.</summary>
    [Fact]
    public async Task ShouldRejectDocumentationGivenOmittedExplicitScheme()
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        _ = builder.Services.AddAuthorization(options => options.AddPolicy("named", policy => policy.RequireAuthenticatedUser()));
        _ = builder.Services.AddPortia().AddHttp(options =>
        {
            options.OpenApi.SecuritySchemes.Add("mapped", new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "bearer" });
            _ = options.OpenApi.SecurityPolicy("named", "mapped");
        }).AddRequestHandler<ContractMetadataHandler>();
        await using var app = builder.Build();
        _ = app.MapGroup("").AllowAnonymous().MapPortiaOpenApi();
        _ = app.MapPortiaGet<ContractMetadataRequest, ContractMetadataModel>("/explicit")
            .RequireAuthorization(new AuthorizeAttribute { Policy = "named", AuthenticationSchemes = "omitted" });
        await app.StartAsync();

        // Act
        var exception = await Record.ExceptionAsync(() => app.GetTestClient().GetStringAsync("/openapi/v1.json"));

        // Assert
        var failure = Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("omitted", failure.Message, StringComparison.Ordinal);
    }

}
