using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Cntryl.Portia.Tests;

[Collection("MCP HTTP integration")]
public sealed class McpPrimitiveTests
{
    [Theory]
    [InlineData("--resource-only", "resources", "resources/templates/list")]
    [InlineData("--prompt-only", "prompts", "prompts/list")]
    public async Task ShouldEmitPrivateZeroTtlHintsGivenModernProtocol(string mode, string capability,
        string listMethod)
    {
        // Arrange
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
                            ?? throw new InvalidOperationException("Missing test configuration.");
        var host = Path.GetFullPath(
            $"../../../../../smoke/Portia.McpStdioHost/bin/{configuration}/net11.0/Portia.McpStdioHost.dll",
            AppContext.BaseDirectory);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        process.StartInfo.ArgumentList.Add(host);

        // Act
        process.StartInfo.ArgumentList.Add(mode);

        // Assert
        Assert.True(process.Start());
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            const string meta = "\"_meta\":{\"io.modelcontextprotocol/protocolVersion\":\"2026-07-28\",\"io.modelcontextprotocol/clientCapabilities\":{}}";
            await process.StandardInput.WriteLineAsync($"{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"server/discover\",\"params\":{{{meta}}}}}");
            await process.StandardInput.FlushAsync(timeout.Token);
            using (var discovery = JsonDocument.Parse((await process.StandardOutput.ReadLineAsync(timeout.Token))!))
            {
                var supported = discovery.RootElement.GetProperty("result").GetProperty("capabilities")
                    .GetProperty(capability);
                Assert.False(supported.TryGetProperty("listChanged", out _));
                Assert.False(supported.TryGetProperty("subscribe", out _));
            }
            await process.StandardInput.WriteLineAsync($"{{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"{listMethod}\",\"params\":{{{meta}}}}}");
            await process.StandardInput.FlushAsync(timeout.Token);
            using var listed = JsonDocument.Parse((await process.StandardOutput.ReadLineAsync(timeout.Token))!);
            var result = listed.RootElement.GetProperty("result");
            Assert.Equal(0, result.GetProperty("ttlMs").GetInt32());
            Assert.Equal("private", result.GetProperty("cacheScope").GetString());
            if (mode == "--resource-only")
            {
                await process.StandardInput.WriteLineAsync($"{{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"resources/read\",\"params\":{{\"uri\":\"greetings://stdio/Portia\",{meta}}}}}");
                await process.StandardInput.FlushAsync(timeout.Token);
                using var read = JsonDocument.Parse((await process.StandardOutput.ReadLineAsync(timeout.Token))!);
                var content = read.RootElement.GetProperty("result");
                Assert.Equal(0, content.GetProperty("ttlMs").GetInt32());
                Assert.Equal("private", content.GetProperty("cacheScope").GetString());
            }
        }
        finally
        {
            if (!process.HasExited)
                process.Kill();
            await process.WaitForExitAsync();
        }
    }

    [Theory]
    [InlineData("--resource-only")]
    [InlineData("--prompt-only")]
    public async Task ShouldServeSinglePrimitiveGivenStdioHost(string mode)
    {
        // Arrange
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
                            ?? throw new InvalidOperationException("Missing test configuration.");
        var host = Path.GetFullPath(
            $"../../../../../smoke/Portia.McpStdioHost/bin/{configuration}/net11.0/Portia.McpStdioHost.dll",
            AppContext.BaseDirectory);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = "dotnet",
            Arguments = [host, mode],
            Name = "Portia primitive stdio test"
        }, NullLoggerFactory.Instance);

        // Act
        await using var client = await McpClient.CreateAsync(transport, StdioClientOptions.Create(),
            cancellationToken: timeout.Token);

        // Assert
        if (mode == "--resource-only")
        {
            Assert.Single(await client.ListResourceTemplatesAsync(cancellationToken: timeout.Token));
            var result = await client.ReadResourceAsync("greetings://stdio/Portia", cancellationToken: timeout.Token);
            Assert.Contains("stdio-test-actor", Assert.IsType<TextResourceContents>(Assert.Single(result.Contents)).Text,
                StringComparison.Ordinal);
        }
        else
        {
            Assert.Single(await client.ListPromptsAsync(cancellationToken: timeout.Token));
            var result = await client.GetPromptAsync("stdio-greeting",
                new Dictionary<string, object?> { ["name"] = "Portia" }, cancellationToken: timeout.Token);
            Assert.Contains("stdio-test-actor", Assert.IsType<TextContentBlock>(Assert.Single(result.Messages).Content).Text,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ShouldServeResourceGivenResourceOnlyHostWhenRoutedThroughRequestBus()
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        _ = builder.Services.AddPortia()
            .AddRequestHandler<McpRegistrationTests.ReadGreetingHandler>()
            .AddMcpResource<McpRegistrationTests.ReadGreeting, string>("greetings://local/{name}",
                values => new McpRegistrationTests.ReadGreeting(values["name"]),
                options =>
                {
                    options.Authenticated();
                    options.Named("Greeting resource");
                    options.Titled("Greeting");
                    options.DescribedAs("Reads a greeting as resource text.");
                    options.AsText("text/plain", value => value);
                })
            .AddMcpHttp();
        await using var app = builder.Build();
        app.Use((context, next) =>
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity("test"));
            return next(context);
        });
        _ = app.MapPortiaMcp().AllowAnonymous();
        await app.StartAsync();
        // Template discovery and read are exercised through the official client below.
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri("http://localhost/mcp")
        }, app.GetTestClient(), null);
        await using var raw = await McpClient.CreateAsync(transport);

        // Act
        var templates = await raw.ListResourceTemplatesAsync();

        // Assert
        var template = Assert.Single(templates);
        Assert.Equal("greetings://local/{name}", template.UriTemplate);
        Assert.Equal("Greeting resource", template.Name);
        Assert.Equal("Greeting", template.Title);
        Assert.Equal("Reads a greeting as resource text.", template.Description);
        var result = await raw.ReadResourceAsync("greetings://local/Portia");
        Assert.Equal("Hello, Portia.", Assert.IsType<TextResourceContents>(Assert.Single(result.Contents)).Text);
    }

    [Fact]
    public async Task ShouldIndexFixedPrimitivesAndPreserveTemplateOrderGivenMixedResourcesAndPrompts()
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        _ = builder.Services.AddPortia()
            .AddRequestHandler<McpRegistrationTests.ReadGreetingHandler>()
            .AddMcpResource<McpRegistrationTests.ReadGreeting, string>("greetings://index/fixed-first",
                _ => new McpRegistrationTests.ReadGreeting("fixed-first"),
                options => { options.Public(); options.AsText("text/plain", static value => value); })
            .AddMcpResource<McpRegistrationTests.ReadGreeting, string>("greetings://index/fixed-second",
                _ => new McpRegistrationTests.ReadGreeting("fixed-second"),
                options => { options.Public(); options.AsText("text/plain", static value => value); })
            .AddMcpResource<McpRegistrationTests.ReadGreeting, string>("greetings://index/template/{name}",
                values => new McpRegistrationTests.ReadGreeting($"template:{values["name"]}"),
                options => { options.Public(); options.AsText("text/plain", static value => value); })
            .AddMcpResource<McpRegistrationTests.ReadGreeting, string>("greetings://index/alternate/{name}",
                values => new McpRegistrationTests.ReadGreeting($"alternate:{values["name"]}"),
                options => { options.Public(); options.AsText("text/plain", static value => value); })
            .AddMcpPrompt<McpRegistrationTests.ReadGreeting, string>("prompt-first",
                values => new McpRegistrationTests.ReadGreeting($"first:{values["name"]}"),
                value => [new McpPromptMessage("user", value)],
                options => { options.Public(); options.Required("name"); })
            .AddMcpPrompt<McpRegistrationTests.ReadGreeting, string>("prompt-last",
                values => new McpRegistrationTests.ReadGreeting($"last:{values["name"]}"),
                value => [new McpPromptMessage("user", value)],
                options => { options.Public(); options.Required("name"); })
            .AddMcpHttp();
        await using var app = builder.Build();
        _ = app.MapPortiaMcp().AllowAnonymous();
        await app.StartAsync();
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri("http://localhost/mcp")
        }, app.GetTestClient(), null);
        await using var client = await McpClient.CreateAsync(transport);

        // Act
        var resources = await client.ListResourcesAsync();

        // Assert
        Assert.Equal(["greetings://index/fixed-first", "greetings://index/fixed-second"],
            resources.Select(resource => resource.Uri));
        var templates = await client.ListResourceTemplatesAsync();
        Assert.Equal(["greetings://index/template/{name}", "greetings://index/alternate/{name}"],
            templates.Select(resource => resource.UriTemplate));
        var firstFixed = await client.ReadResourceAsync("greetings://index/fixed-first");
        Assert.Equal("Hello, fixed-first.", Assert.IsType<TextResourceContents>(Assert.Single(firstFixed.Contents)).Text);
        var lastTemplate = await client.ReadResourceAsync("greetings://index/alternate/Portia");
        Assert.Equal("Hello, alternate:Portia.", Assert.IsType<TextResourceContents>(Assert.Single(lastTemplate.Contents)).Text);
        var escapedTemplate = await client.ReadResourceAsync("greetings://index/template/Portia%20Smith");
        Assert.Equal("Hello, template:Portia Smith.",
            Assert.IsType<TextResourceContents>(Assert.Single(escapedTemplate.Contents)).Text);

        var prompts = await client.ListPromptsAsync();
        Assert.Equal(["prompt-first", "prompt-last"], prompts.Select(prompt => prompt.Name));
        var firstPrompt = await client.GetPromptAsync("prompt-first", new Dictionary<string, object?> { ["name"] = "Portia" });
        var lastPrompt = await client.GetPromptAsync("prompt-last", new Dictionary<string, object?> { ["name"] = "Portia" });
        Assert.Equal("Hello, first:Portia.", Assert.IsType<TextContentBlock>(Assert.Single(firstPrompt.Messages).Content).Text);
        Assert.Equal("Hello, last:Portia.", Assert.IsType<TextContentBlock>(Assert.Single(lastPrompt.Messages).Content).Text);
    }

    [Fact]
    public async Task ShouldServePromptGivenPromptOnlyHostWhenRoutedThroughRequestBus()
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        _ = builder.Services.AddPortia()
            .AddRequestHandler<McpRegistrationTests.ReadGreetingHandler>()
            .AddMcpPrompt<McpRegistrationTests.ReadGreeting, string>("greeting",
                values => new McpRegistrationTests.ReadGreeting(values["name"]),
                value => [new McpPromptMessage("user", value)],
                options =>
                {
                    options.Authenticated();
                    options.Titled("Greeting prompt");
                    options.DescribedAs("Renders a greeting for a supplied name.");
                    options.Required("name", "The person whose greeting should be read.");
                })
            .AddMcpHttp();
        await using var app = builder.Build();
        app.Use((context, next) =>
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity("test"));
            return next(context);
        });
        _ = app.MapPortiaMcp().AllowAnonymous();
        await app.StartAsync();
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri("http://localhost/mcp")
        }, app.GetTestClient(), null);

        // Act
        await using var client = await McpClient.CreateAsync(transport);

        // Assert
        var prompt = Assert.Single(await client.ListPromptsAsync());
        Assert.Equal("greeting", prompt.Name);
        Assert.Equal("Greeting prompt", prompt.ProtocolPrompt.Title);
        Assert.Equal("Renders a greeting for a supplied name.", prompt.ProtocolPrompt.Description);
        Assert.Equal("The person whose greeting should be read.", Assert.Single(prompt.ProtocolPrompt.Arguments!).Description);
        var result = await client.GetPromptAsync("greeting", new Dictionary<string, object?> { ["name"] = "Portia" });
        Assert.Equal("Hello, Portia.", Assert.IsType<TextContentBlock>(Assert.Single(result.Messages).Content).Text);
    }

    [Fact]
    public async Task ShouldExposeArgumentDescriptionGivenOptionalPromptArgument()
    {
        // Arrange
        const string argumentDescription = "Name to greet; defaults to Guest when omitted.";
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        _ = builder.Services.AddPortia()
            .AddRequestHandler<McpRegistrationTests.ReadGreetingHandler>()
            .AddMcpPrompt<McpRegistrationTests.ReadGreeting, string>("optional-greeting",
                values => new McpRegistrationTests.ReadGreeting(
                    values.TryGetValue("name", out var name) ? name : "Guest"),
                value => [new McpPromptMessage("user", value)],
                options =>
                {
                    options.Public();
                    options.Optional("name", argumentDescription);
                })
            .AddMcpHttp();
        await using var app = builder.Build();
        _ = app.MapPortiaMcp().AllowAnonymous();
        await app.StartAsync();
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri("http://localhost/mcp")
        }, app.GetTestClient(), null);

        // Act
        await using var client = await McpClient.CreateAsync(transport);

        // Assert
        var prompt = Assert.Single(await client.ListPromptsAsync());
        var argument = Assert.Single(prompt.ProtocolPrompt.Arguments!);
        Assert.Equal("name", argument.Name);
        Assert.Equal(argumentDescription, argument.Description);
        Assert.Equal(false, argument.Required);

        var result = await client.GetPromptAsync("optional-greeting", new Dictionary<string, object?>());
        Assert.Equal("Hello, Guest.", Assert.IsType<TextContentBlock>(Assert.Single(result.Messages).Content).Text);
    }

    [Fact]
    public async Task ShouldHideFixedResourceAndPromptGivenOtherActor()
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<NamedVisibilityPolicy>();
        _ = builder.Services.AddPortia()
            .AddRequestHandler<McpRegistrationTests.ReadGreetingHandler>()
            .AddMcpResource<McpRegistrationTests.ReadGreeting, string>("greetings://local/fixed",
                _ => new McpRegistrationTests.ReadGreeting("Owner"),
                options => { options.VisibleTo<NamedVisibilityPolicy>(); options.AsJson(); })
            .AddMcpPrompt<McpRegistrationTests.ReadGreeting, string>("owner-greeting",
                _ => new McpRegistrationTests.ReadGreeting("Owner"),
                value => [new McpPromptMessage("user", value)],
                options => options.VisibleTo<NamedVisibilityPolicy>())
            .AddMcpHttp();
        await using var app = builder.Build();
        app.Use((context, next) =>
        {
            var name = context.Request.Headers["X-Actor"].ToString();
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, name)], "test"));
            return next(context);
        });
        _ = app.MapPortiaMcp().AllowAnonymous();
        await app.StartAsync();

        using var outsiderHttp = app.GetTestClient();
        outsiderHttp.DefaultRequestHeaders.Add("X-Actor", "outsider");
        var outsiderTransport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri("http://localhost/mcp")
        }, outsiderHttp, null);

        // Act
        await using var outsider = await McpClient.CreateAsync(outsiderTransport);

        // Assert
        Assert.Empty(await outsider.ListResourcesAsync());
        Assert.Empty(await outsider.ListPromptsAsync());
        var resourceError = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await outsider.ReadResourceAsync("greetings://local/fixed"));
        var missingResource = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await outsider.ReadResourceAsync("greetings://local/missing"));
        var promptError = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await outsider.GetPromptAsync("owner-greeting"));
        var missingPrompt = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await outsider.GetPromptAsync("missing-prompt"));
        Assert.Equal(missingResource.Message, resourceError.Message);
        Assert.Equal(missingPrompt.Message, promptError.Message);
        Assert.DoesNotContain("Owner", resourceError.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Owner", promptError.Message, StringComparison.Ordinal);

        using var ownerHttp = app.GetTestClient();
        ownerHttp.DefaultRequestHeaders.Add("X-Actor", "owner");
        var ownerTransport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri("http://localhost/mcp")
        }, ownerHttp, null);
        await using var owner = await McpClient.CreateAsync(ownerTransport);
        Assert.Single(await owner.ListResourcesAsync());
        Assert.Single(await owner.ListPromptsAsync());
        var resource = await owner.ReadResourceAsync("greetings://local/fixed");
        Assert.Equal("\"Hello, Owner.\"", Assert.IsType<TextResourceContents>(Assert.Single(resource.Contents)).Text);
    }

    public sealed class NamedVisibilityPolicy : IMcpVisibilityPolicy
    {
        public ValueTask<bool> IsVisibleAsync(ClaimsPrincipal actor, CancellationToken cancellationToken) =>
            ValueTask.FromResult(actor.Identity?.Name == "owner");
    }

    [Fact]
    public async Task ShouldApplyTenantAuthorizerGivenGuessedResourceAndPrompt()
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        _ = builder.Services.AddPortia()
            .AddRequestHandler<ReadTenantHandler>()
            .AddRequestAuthorizer<ReadTenantAuthorizer>()
            .AddMcpResource<ReadTenant, string>("tenant://records/{tenant}",
                values => new ReadTenant(values["tenant"]),
                options => { options.Authenticated(); options.AsText("text/plain", value => value); })
            .AddMcpPrompt<ReadTenant, string>("tenant-summary",
                values => new ReadTenant(values["tenant"]),
                value => [new McpPromptMessage("user", value)],
                options => { options.Authenticated(); options.Required("tenant"); })
            .AddMcpHttp();
        await using var app = builder.Build();
        app.Use((context, next) =>
        {
            var name = context.Request.Headers["X-Actor"].ToString();
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, name)], "test"));
            return next(context);
        });
        _ = app.MapPortiaMcp().AllowAnonymous();
        await app.StartAsync();
        using var http = app.GetTestClient();
        http.DefaultRequestHeaders.Add("X-Actor", "tenant-a");
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri("http://localhost/mcp")
        }, http, null);

        // Act
        await using var client = await McpClient.CreateAsync(transport);

        // Assert
        Assert.Single(await client.ListResourceTemplatesAsync());
        Assert.Single(await client.ListPromptsAsync());
        var owned = await client.ReadResourceAsync("tenant://records/tenant-a");
        Assert.Equal("tenant-a:tenant-a", Assert.IsType<TextResourceContents>(Assert.Single(owned.Contents)).Text);
        var foreignResource = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await client.ReadResourceAsync("tenant://records/tenant-b"));
        var foreignPrompt = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await client.GetPromptAsync("tenant-summary", new Dictionary<string, object?> { ["tenant"] = "tenant-b" }));
        Assert.DoesNotContain("secret", foreignResource.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", foreignPrompt.Message, StringComparison.Ordinal);
    }

    public sealed record ReadTenant(string Tenant) : IRequest<string>, ICallable;

    public sealed class ReadTenantHandler : IRequestHandler<ReadTenant, string>
    {
        public ValueTask<Result<string>> HandleAsync(IRequestContext<ReadTenant> context, CancellationToken ct) =>
            ValueTask.FromResult(Result<string>.Success($"{context.Request.Tenant}:{context.Actor.Identity?.Name}"));
    }

    public sealed class ReadTenantAuthorizer : IRequestAuthorizer<ReadTenant>
    {
        public ValueTask<Result> AuthorizeAsync(IRequestContext<ReadTenant> context, CancellationToken ct) =>
            ValueTask.FromResult(context.Actor.Identity?.Name == context.Request.Tenant
                ? Result.Success
                : Result.Failure(new RequestError(RequestErrorKind.Forbidden, "secret tenant claim")));
    }

    [Theory]
    [InlineData("greetings://local/{name}/../item")]
    [InlineData("greetings://local/{name}/{name}")]
    [InlineData("greetings://local/a%2fb")]
    [InlineData("greetings://local/a?x=1")]
    [InlineData("greetings://local/a#part")]
    [InlineData("greetings://{host}/item")]
    public void ShouldRejectTemplateGivenUnsafeResourceUri(string uri)
    {
        // Arrange
        var application = new ServiceCollection().AddPortia();

        // Act
        var exception = Record.Exception(() => application.AddMcpResource<McpRegistrationTests.ReadGreeting, string>(
            uri, _ => new McpRegistrationTests.ReadGreeting("Portia"),
            options => { options.Public(); options.AsJson(); }));

        // Assert
        Assert.IsType<ArgumentException>(exception);
    }

    [Fact]
    public void ShouldRejectPrimitiveDeclarationsGivenHttpTransportAlreadyActivated()
    {
        // Arrange
        var application = new ServiceCollection().AddPortia()
            .AddMcpTool<McpRegistrationTests.ReadGreeting>()
            .AddMcpHttp();

        // Act
        var resource = Record.Exception(() =>
            application.AddMcpResource<McpRegistrationTests.ReadGreeting, string>(
                "greetings://local/fixed", _ => new McpRegistrationTests.ReadGreeting("Portia"),
                options => { options.Public(); options.AsJson(); }));
        var prompt = Record.Exception(() =>
            application.AddMcpPrompt<McpRegistrationTests.ReadGreeting, string>(
                "greeting", _ => new McpRegistrationTests.ReadGreeting("Portia"),
                value => [new McpPromptMessage("user", value)], options => options.Public()));

        // Assert
        var resourceException = Assert.IsType<InvalidOperationException>(resource);
        var promptException = Assert.IsType<InvalidOperationException>(prompt);
        Assert.Contains("before AddMcpHttp", resourceException.Message, StringComparison.Ordinal);
        Assert.Contains("before AddMcpHttp", promptException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ShouldRejectPrimitiveDeclarationsGivenStdioTransportAlreadyActivated()
    {
        // Arrange
        var application = new ServiceCollection().AddPortia()
            .AddMcpTool<McpRegistrationTests.ReadGreeting>()
            .AddMcpStdio(options => options.UseLocalDevelopmentActor());

        // Act
        var resource = Record.Exception(() =>
            application.AddMcpResource<McpRegistrationTests.ReadGreeting, string>(
                "greetings://local/fixed", _ => new McpRegistrationTests.ReadGreeting("Portia"),
                options => { options.Public(); options.AsJson(); }));
        var prompt = Record.Exception(() =>
            application.AddMcpPrompt<McpRegistrationTests.ReadGreeting, string>(
                "greeting", _ => new McpRegistrationTests.ReadGreeting("Portia"),
                value => [new McpPromptMessage("user", value)], options => options.Public()));

        // Assert
        var resourceException = Assert.IsType<InvalidOperationException>(resource);
        var promptException = Assert.IsType<InvalidOperationException>(prompt);
        Assert.Contains("before AddMcpStdio", resourceException.Message, StringComparison.Ordinal);
        Assert.Contains("before AddMcpStdio", promptException.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("greetings://local/fixed")]
    [InlineData("greetings://local/{name}")]
    public void ShouldKeepRegisteredLimitGivenLowerUriLimitRejected(string uri)
    {
        // Arrange
        var application = new ServiceCollection().AddPortia()
            .AddMcpResource<McpRegistrationTests.ReadGreeting, string>(
                uri, _ => new McpRegistrationTests.ReadGreeting("Portia"),
                options => { options.Public(); options.AsJson(); });

        // Act
        var exception = Record.Exception(() =>
            application.ConfigureMcpLimits(limits =>
                limits.MaxResourceUriBytes = System.Text.Encoding.UTF8.GetByteCount(uri) - 1));

        // Assert
        Assert.IsType<ArgumentOutOfRangeException>(exception);
        using var services = application.Services.BuildServiceProvider();
        Assert.Equal(2048, services.GetRequiredService<McpLimits>().MaxResourceUriBytes);
    }

    [Theory]
    [InlineData("greetings://local/fixed")]
    [InlineData("greetings://local/{name}")]
    public void ShouldAcceptLimitGivenUriLimitEqualToRegisteredUriBytes(string uri)
    {
        // Arrange
        var application = new ServiceCollection().AddPortia()
            .AddMcpResource<McpRegistrationTests.ReadGreeting, string>(
                uri, _ => new McpRegistrationTests.ReadGreeting("Portia"),
                options => { options.Public(); options.AsJson(); });
        using var services = application.Services.BuildServiceProvider();

        // Act
        _ = application.ConfigureMcpLimits(limits =>
            limits.MaxResourceUriBytes = System.Text.Encoding.UTF8.GetByteCount(uri));

        // Assert
        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(uri),
            services.GetRequiredService<McpLimits>().MaxResourceUriBytes);
    }

    [Fact]
    public void ShouldRejectRegistrationGivenAmbiguousResourceTemplateAndFixedUri()
    {
        // Arrange
        var application = new ServiceCollection().AddPortia();

        // Act
        _ = application.AddMcpResource<McpRegistrationTests.ReadGreeting, string>("greetings://local/{name}",
            values => new McpRegistrationTests.ReadGreeting(values["name"]),
            options => { options.Public(); options.AsJson(); });

        // Assert
        Assert.Throws<InvalidOperationException>(() => application.AddMcpResource<McpRegistrationTests.ReadGreeting, string>(
            "greetings://local/Portia", _ => new McpRegistrationTests.ReadGreeting("Portia"),
            options => { options.Public(); options.AsJson(); }));
    }

    [Fact]
    public void ShouldRejectTemplateGivenOverlapWithDifferentPlaceholderPosition()
    {
        // Arrange
        var application = new ServiceCollection().AddPortia();

        // Act
        _ = application.AddMcpResource<McpRegistrationTests.ReadGreeting, string>("greetings://local/{name}/item",
            values => new McpRegistrationTests.ReadGreeting(values["name"]),
            options => { options.Public(); options.AsJson(); });

        // Assert
        Assert.Throws<InvalidOperationException>(() => application.AddMcpResource<McpRegistrationTests.ReadGreeting, string>(
            "greetings://local/alice/{item}", _ => new McpRegistrationTests.ReadGreeting("Alice"),
            options => { options.Public(); options.AsJson(); }));
    }

    [Fact]
    public void ShouldRejectRegistrationGivenEncodedAliasOfFixedResource()
    {
        // Arrange
        var application = new ServiceCollection().AddPortia();

        // Act
        _ = application.AddMcpResource<McpRegistrationTests.ReadGreeting, string>("greetings://local/A",
            _ => new McpRegistrationTests.ReadGreeting("A"),
            options => { options.Public(); options.AsJson(); });

        // Assert
        Assert.Throws<InvalidOperationException>(() => application.AddMcpResource<McpRegistrationTests.ReadGreeting, string>(
            "greetings://local/%41", _ => new McpRegistrationTests.ReadGreeting("A"),
            options => { options.Public(); options.AsJson(); }));
    }

    [Fact]
    public void ShouldAcceptRegistrationGivenFixedAbsoluteUriWithoutPath()
    {
        // Arrange
        var application = new ServiceCollection().AddPortia();

        // Act
        var exception = Record.Exception(() => application.AddMcpResource<McpRegistrationTests.ReadGreeting, string>(
            "greetings://local",
            _ => new McpRegistrationTests.ReadGreeting("Portia"),
            options => { options.Public(); options.AsJson(); }));

        // Assert
        Assert.Null(exception);
    }

    [Fact]
    public void ShouldRejectTemplateGivenUriBeyondDefaultLimit()
    {
        // Arrange
        var application = new ServiceCollection().AddPortia();

        // Act
        var uri = "greetings://local/" + new string('a', 2048);

        // Assert
        Assert.Throws<ArgumentException>(() => application.AddMcpResource<McpRegistrationTests.ReadGreeting, string>(
            uri, _ => new McpRegistrationTests.ReadGreeting("Portia"),
            options => { options.Public(); options.AsJson(); }));
    }

    [Fact]
    public async Task ShouldReturnBinaryResourceAndRejectUnknownArgumentsGivenBinaryResourceAndPrompt()
    {
        // Arrange
        var bound = 0;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        _ = builder.Services.AddPortia()
            .AddRequestHandler<McpRegistrationTests.ReadGreetingHandler>()
            .AddMcpResource<McpRegistrationTests.ReadGreeting, string>("greetings://local/image",
                _ => new McpRegistrationTests.ReadGreeting("Portia"),
                options =>
                {
                    options.Public();
                    options.AsBinary("application/octet-stream",
                    value => System.Text.Encoding.UTF8.GetBytes(value));
                })
            .AddMcpPrompt<McpRegistrationTests.ReadGreeting, string>("bound-greeting",
                values => { Interlocked.Increment(ref bound); return new McpRegistrationTests.ReadGreeting(values["name"]); },
                value => [new McpPromptMessage("user", value)],
                options => { options.Public(); options.Required("name"); })
            .AddMcpHttp();
        await using var app = builder.Build();
        _ = app.MapPortiaMcp().AllowAnonymous();
        await app.StartAsync();
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri("http://localhost/mcp")
        }, app.GetTestClient(), null);
        await using var client = await McpClient.CreateAsync(transport);

        // Act
        var result = await client.ReadResourceAsync("greetings://local/image");

        // Assert
        Assert.Equal("Hello, Portia.", System.Text.Encoding.UTF8.GetString(
            Assert.IsType<BlobResourceContents>(Assert.Single(result.Contents)).DecodedData.Span));
        _ = await Assert.ThrowsAnyAsync<Exception>(async () => await client.GetPromptAsync("bound-greeting"));
        _ = await Assert.ThrowsAnyAsync<Exception>(async () => await client.GetPromptAsync("bound-greeting",
            new Dictionary<string, object?> { ["name"] = "Portia", ["extra"] = "bad" }));
        Assert.Equal(0, bound);
    }

    [Theory]
    [InlineData("greetings://local/%2f")]
    [InlineData("greetings://local/%5c")]
    [InlineData("greetings://local/%2e")]
    [InlineData("greetings://local/%ZZ")]
    [InlineData("greetings://local/../item")]
    public async Task ShouldRejectReadBeforeBindingGivenUnsafeUri(string uri)
    {
        // Arrange
        var bound = 0;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        _ = builder.Services.AddPortia()
            .AddRequestHandler<McpRegistrationTests.ReadGreetingHandler>()
            .AddMcpResource<McpRegistrationTests.ReadGreeting, string>("greetings://local/{name}",
                values => { Interlocked.Increment(ref bound); return new McpRegistrationTests.ReadGreeting(values["name"]); },
                options => { options.Public(); options.AsText("text/plain", value => value); })
            .AddMcpHttp();
        await using var app = builder.Build();
        _ = app.MapPortiaMcp().AllowAnonymous();
        await app.StartAsync();
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri("http://localhost/mcp")
        }, app.GetTestClient(), null);

        // Act
        await using var client = await McpClient.CreateAsync(transport);

        // Assert
        _ = await Assert.ThrowsAnyAsync<Exception>(async () => await client.ReadResourceAsync(uri));
        Assert.Equal(0, bound);
    }

    [Fact]
    public async Task ShouldEnforceLimitsGivenConfiguredPromptAndOutputLimits()
    {
        // Arrange
        var bound = 0;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        _ = builder.Services.AddPortia()
            .AddRequestHandler<McpRegistrationTests.ReadGreetingHandler>()
            .AddMcpTool<McpRegistrationTests.ReadGreeting>()
            .AddMcpResource<McpRegistrationTests.ReadGreeting, string>("greetings://local/fixed",
                _ => new McpRegistrationTests.ReadGreeting("Portia"),
                options => { options.Public(); options.AsText("text/plain", value => value); })
            .AddMcpPrompt<McpRegistrationTests.ReadGreeting, string>("too-many-messages",
                _ => new McpRegistrationTests.ReadGreeting("Portia"),
                value => [new McpPromptMessage("user", value), new McpPromptMessage("assistant", value)],
                options => options.Public())
            .AddMcpPrompt<McpRegistrationTests.ReadGreeting, string>("too-many-argument-bytes",
                values => { Interlocked.Increment(ref bound); return new McpRegistrationTests.ReadGreeting(values["name"]); },
                value => [new McpPromptMessage("user", value)],
                options => { options.Public(); options.Required("name"); })
            .ConfigureMcpLimits(limits =>
            {
                limits.MaxPromptArgumentBytes = 12;
                limits.MaxPromptMessages = 1;
                limits.MaxResultBytes = 140;
            })
            .AddMcpHttp();
        await using var app = builder.Build();
        _ = app.MapPortiaMcp().AllowAnonymous();
        await app.StartAsync();
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri("http://localhost/mcp")
        }, app.GetTestClient(), null);

        // Act
        await using var client = await McpClient.CreateAsync(transport);

        // Assert
        _ = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await client.ReadResourceAsync("greetings://local/fixed"));
        _ = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await client.GetPromptAsync("too-many-messages"));
        _ = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await client.GetPromptAsync("too-many-argument-bytes",
                new Dictionary<string, object?> { ["name"] = "Portia" }));
        var tool = await client.CallToolAsync("greetings.read",
            new Dictionary<string, object?> { ["name"] = "Portia" });
        Assert.True(tool.IsError);
        Assert.Equal(0, bound);
    }

    [Fact]
    public async Task ShouldCancelResourceDispatchGivenConfiguredOperationDeadline()
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        _ = builder.Services.AddPortia()
            .AddRequestHandler<SlowReadHandler>()
            .AddMcpResource<SlowRead, string>("slow://local/item",
                _ => new SlowRead(),
                options => { options.Public(); options.AsText("text/plain", value => value); })
            .ConfigureMcpLimits(limits => limits.OperationDeadline = TimeSpan.FromMilliseconds(100))
            .AddMcpHttp();
        await using var app = builder.Build();
        _ = app.MapPortiaMcp().AllowAnonymous();
        await app.StartAsync();
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri("http://localhost/mcp")
        }, app.GetTestClient(), null);
        await using var client = await McpClient.CreateAsync(transport);

        // Act
        var started = Stopwatch.StartNew();

        // Assert
        var error = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await client.ReadResourceAsync("slow://local/item"));
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(2));
        Assert.DoesNotContain("TaskCanceledException", error.Message, StringComparison.Ordinal);
    }

    public sealed record SlowRead : IRequest<string>, ICallable;

    public sealed class SlowReadHandler : IRequestHandler<SlowRead, string>
    {
        public async ValueTask<Result<string>> HandleAsync(IRequestContext<SlowRead> context, CancellationToken ct)
        {
            await Task.Delay(TimeSpan.FromMinutes(1), ct);
            return Result<string>.Success("late");
        }
    }
}
