using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Cntryl.Portia.McpContracts;
using Cntryl.Portia.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Cntryl.Portia.Tests;

[Collection("MCP HTTP integration")]
public sealed class TransportParityTests
{
    static readonly string[] SuccessfulStages = ["authorized", "before", "guarded", "handled", "after"];
    static readonly string[] DeniedStages = ["authorized", "disposed"];
    static readonly int[] CommittedValues = [1, 2];
    [Theory]
    [InlineData("direct")]
    [InlineData("rest")]
    [InlineData("mcp-http")]
    [InlineData("mcp-stdio")]
    public async Task ShouldPreserveTheSameApplicationPipelineAndCommittedEvents(string transportName)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IEventStore, InMemoryEventStore>();
        _ = QualifiedApplication.Add(builder.Services).AddHttp()
            .AddMcpTool<QualifiedChange>(tool => tool.ReadOnly().Idempotent())
            .AddMcpTool<QualifiedRead>()
            .AddMcpResource<QualifiedRead, QualifiedState>("qualification://values/{tenant}",
                values => new QualifiedRead(values["tenant"]), options => { options.Authenticated(); options.AsJson(); })
            .AddMcpPrompt<QualifiedRead, QualifiedState>("qualification-read", values => new QualifiedRead(values["tenant"]),
                value => [new McpPromptMessage("user", string.Join(',', value.Values))],
                options => { options.Authenticated(); options.Required("tenant"); })
            .AddMcpHttp();
        await using var app = builder.Build();
        app.UseExceptionHandler(error => error.Run(context =>
        {
            context.Response.StatusCode = 500;
            return context.Response.WriteAsync("Internal failure.");
        }));
        app.Use((context, next) =>
        {
            context.User = Actor();
            return next(context);
        });
        _ = app.MapPortiaPost<QualifiedChange, QualifiedReply>("/qualification").AllowAnonymous();
        _ = app.MapPortiaGet<QualifiedRead, QualifiedState>("/qualification").AllowAnonymous();
        _ = app.MapPortiaMcp().AllowAnonymous();
        await app.StartAsync();
        using var http = app.GetTestClient();
        var json = app.Services.GetRequiredKeyedService<JsonSerializerOptions>(PortiaServiceKeys.Json);
        McpClient? client = null;
        if (transportName == "mcp-http")
            client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
            { Endpoint = new Uri("http://localhost/mcp") }, http));
        else if (transportName == "mcp-stdio")
        {
            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
            var host = Path.GetFullPath(
                $"../../../../../smoke/Portia.McpStdioHost/bin/{configuration}/net10.0/Portia.McpStdioHost.dll",
                AppContext.BaseDirectory);
            client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
            { Command = "dotnet", Arguments = [host, "--qualification"], Name = "Application parity" }));
        }
        await using var ownedClient = client;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;

        var first = await ChangeAsync(new QualifiedChange(QualifiedObservations.Tenant, 1));
        var second = await ChangeAsync(new QualifiedChange(QualifiedObservations.Tenant, 2));
        Assert.Equal(200, first.Status);
        Assert.Equal(200, second.Status);
        Assert.NotEqual(first.Reply!.ScopeId, second.Reply!.ScopeId);
        Assert.Equal(SuccessfulStages, first.Reply.Stages);
        Assert.Equal(first.Reply.Stages, second.Reply.Stages);
        Assert.Equal(400, (await ChangeAsync(new QualifiedChange(QualifiedObservations.Tenant, 0))).Status);
        Assert.Equal(403, (await ChangeAsync(new QualifiedChange(QualifiedObservations.OtherTenant, 3))).Status);
        Assert.Equal(409, (await ChangeAsync(new QualifiedChange(QualifiedObservations.Tenant, 3, "conflict"))).Status);
        Assert.Equal(500, (await ChangeAsync(new QualifiedChange(QualifiedObservations.Tenant, 3, "fault"))).Status);

        var state = await ReadAsync();
        Assert.Equal(CommittedValues, state.Values);
        Assert.Equal(new[] { QualifiedObservations.Tenant, QualifiedObservations.Tenant }, state.Actors);
        Assert.Equal(6, state.Scopes.Length);
        Assert.Equal(6, state.Scopes.Select(scope => scope.Id).Distinct().Count());
        Assert.All(state.Scopes, scope => Assert.Equal("disposed", scope.Stages[^1]));
        Assert.Equal(DeniedStages, Assert.Single(state.Scopes,
            scope => scope.Stages.Length == 2).Stages);
        Assert.Equal(4, state.Scopes.Count(scope => scope.Stages.Contains("handled", StringComparer.Ordinal)));
        if (client is not null)
        {
            var resource = await client.ReadResourceAsync($"qualification://values/{QualifiedObservations.Tenant}",
                cancellationToken: token);
            var text = Assert.IsType<TextResourceContents>(Assert.Single(resource.Contents)).Text;
            Assert.Equal(state.Values, JsonSerializer.Deserialize(text, TypeInfo<QualifiedState>())!.Values);
            var prompt = await client.GetPromptAsync("qualification-read", new Dictionary<string, object?>
            { ["tenant"] = QualifiedObservations.Tenant }, cancellationToken: token);
            Assert.Equal("1,2", Assert.IsType<TextContentBlock>(Assert.Single(prompt.Messages).Content).Text);
            _ = await Assert.ThrowsAnyAsync<Exception>(async () => await client.ReadResourceAsync(
                $"qualification://values/{QualifiedObservations.OtherTenant}", cancellationToken: token));
            _ = await Assert.ThrowsAnyAsync<Exception>(async () => await client.GetPromptAsync("qualification-read",
                new Dictionary<string, object?> { ["tenant"] = QualifiedObservations.OtherTenant }, cancellationToken: token));
            var malformed = await client.CallToolAsync("qualification.change",
                new Dictionary<string, object?> { ["tenant"] = QualifiedObservations.Tenant }, cancellationToken: token);
            Assert.True(malformed.IsError);
            Assert.Equal(6, (await ReadAsync()).Scopes.Length);
        }

        var concurrent = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ =>
            ChangeAsync(new QualifiedChange(QualifiedObservations.Tenant, 3, "conflict"))));
        Assert.All(concurrent, result => Assert.Equal(409, result.Status));
        var afterConcurrency = await ReadAsync();
        Assert.Equal(CommittedValues, afterConcurrency.Values);
        Assert.Equal(22, afterConcurrency.Scopes.Length);
        Assert.Equal(22, afterConcurrency.Scopes.Select(scope => scope.Id).Distinct().Count());
        Assert.All(afterConcurrency.Scopes, scope =>
        {
            Assert.Equal("disposed", scope.Stages[^1]);
            Assert.True(scope.Stages.Count(stage => stage == "handled") <= 1);
        });

        JsonTypeInfo<T> TypeInfo<T>() => (JsonTypeInfo<T>)json.GetTypeInfo(typeof(T));

        async Task<(int Status, QualifiedReply? Reply)> ChangeAsync(QualifiedChange request)
        {
            if (transportName == "direct")
            {
                await using var scope = app.Services.CreateAsyncScope();
                var bus = scope.ServiceProvider.GetRequiredService<IRequestBus>();
                try
                {
                    var result = await bus.DispatchAsync(request, bus.CreateContext(Actor()), token);
                    return result.IsSuccess ? (200, result.Value) : (Status(result.Error.Kind.ToString()), null);
                }
                catch (IOException) { return (500, null); }
            }
            if (transportName == "rest")
            {
                using var content = new StringContent(JsonSerializer.Serialize(request, TypeInfo<QualifiedChange>()),
                    Encoding.UTF8, "application/json");
                using var response = await http.PostAsync("/qualification", content, token);
                var body = await response.Content.ReadAsStringAsync(token);
                Assert.DoesNotContain("sensitive", body, StringComparison.Ordinal);
                return response.StatusCode == HttpStatusCode.OK
                    ? (200, JsonSerializer.Deserialize(body, TypeInfo<QualifiedReply>())) : ((int)response.StatusCode, null);
            }
            var input = JsonSerializer.SerializeToElement(request, TypeInfo<QualifiedChange>()).EnumerateObject()
                .ToDictionary(property => property.Name, property => (object?)property.Value.Clone());
            var call = await client!.CallToolAsync("qualification.change", input, cancellationToken: token);
            if (call.IsError != true)
                return (200, call.StructuredContent!.Value.GetProperty("result").Deserialize(TypeInfo<QualifiedReply>()));
            Assert.DoesNotContain(call.Content.OfType<TextContentBlock>(), content => content.Text.Contains("sensitive", StringComparison.Ordinal));
            var failure = JsonSerializer.SerializeToElement(call.Meta!["portia/error"]);
            return (Status(failure.GetProperty("kind").GetString()!), null);
        }

        async Task<QualifiedState> ReadAsync()
        {
            if (transportName == "direct")
            {
                await using var scope = app.Services.CreateAsyncScope();
                var bus = scope.ServiceProvider.GetRequiredService<IRequestBus>();
                return (await bus.DispatchAsync(new QualifiedRead(QualifiedObservations.Tenant), bus.CreateContext(Actor()), token)).Value;
            }
            if (transportName == "rest")
                return JsonSerializer.Deserialize(await http.GetStringAsync($"/qualification?tenant={QualifiedObservations.Tenant}", token), TypeInfo<QualifiedState>())!;
            var result = await client!.CallToolAsync("qualification.read", new Dictionary<string, object?>
            { ["tenant"] = QualifiedObservations.Tenant }, cancellationToken: token);
            Assert.False(result.IsError);
            return result.StructuredContent!.Value.GetProperty("result").Deserialize(TypeInfo<QualifiedState>())!;
        }
    }

    static ClaimsPrincipal Actor() => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, QualifiedObservations.Tenant),
            new Claim(ClaimTypes.NameIdentifier, QualifiedObservations.Tenant, ClaimValueTypes.String, "qualification")], "qualification"));

    static int Status(string kind) => kind switch
    {
        "Validation" or "Binding" => 400,
        "Forbidden" => 403,
        "Conflict" => 409,
        _ => 500
    };
}
