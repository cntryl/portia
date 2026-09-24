using System.Net;
using System.Security.Claims;
using Cntryl.Portia.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;

namespace Cntryl.Portia.Tests;

[Collection("MCP HTTP integration")]
public sealed class McpScenarioTests
{
    [Fact]
    public async Task ShouldSnapshotCatalogAndExerciseRealDispatchFailures()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        _ = builder.Services.AddPortia()
            .AddRequestHandler<McpRegistrationTests.ReadGreetingHandler>()
            .AddRequestHandler<McpRegistrationTests.RejectGreetingHandler>()
            .AddRequestHandler<McpRegistrationTests.ReadActorHandler>()
            .AddMcpTool<McpRegistrationTests.ReadGreeting>(tool => tool.ReadOnly().Titled("Read greeting"))
            .AddMcpTool<McpRegistrationTests.RejectGreeting>()
            .AddMcpTool<McpRegistrationTests.ReadActor>()
            .AddMcpHttp();
        await using var app = builder.Build();
        app.Use((context, next) =>
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "scenario-agent")], "test"));
            return next(context);
        });
        _ = app.MapPortiaMcp();
        await app.StartAsync();
        using var http = app.GetTestClient();
        await using (var scenario = await McpScenario.ConnectAsync(http, new Uri("http://localhost/mcp")))
        {
            var tools = await scenario.ListTools().ExpectExactly(
                "actors.read", "greetings.read", "greetings.reject");
            var greeting = Assert.Single(tools, tool => tool.Name == "greetings.read");
            Assert.Equal("Reads a greeting without changing application state.", greeting.Description);
            Assert.Equal("Read greeting", greeting.Title);
            Assert.True(greeting.ReadOnly);
            Assert.Equal("object", greeting.InputSchema.GetProperty("type").GetString());

            var actor = await scenario.When("actors.read").ExpectSuccess();
            Assert.Equal("scenario-agent|mcp|actors.read",
                actor.StructuredJson?.GetProperty("result").GetString());

            var failure = await scenario.When("greetings.reject").ExpectFailure("Conflict");
            Assert.Null(failure.StructuredJson);
            Assert.Equal("Greeting already exists.", failure.Error?.GetProperty("message").GetString());

            var mismatch = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await scenario.When("greetings.reject").ExpectFailure("NotFound"));
            Assert.Contains("'Conflict'", mismatch.Message, StringComparison.Ordinal);
            Assert.Contains("Greeting already exists.", mismatch.Message, StringComparison.Ordinal);

            var malformed = await scenario.When("greetings.read", new Dictionary<string, object?>
            {
                ["name"] = 42
            }).ExpectFailure("Binding");
            Assert.True(malformed.IsError);
        }

        using var response = await http.GetAsync("/not-found");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}

[CollectionDefinition("MCP HTTP integration", DisableParallelization = true)]
public sealed class McpHttpIntegrationGroup;
