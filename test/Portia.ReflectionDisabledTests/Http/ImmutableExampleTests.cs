using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia.ReflectionDisabled;

public sealed class ImmutableExampleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldPublishTypedInputExampleGivenGetterOnlyConstructorMembers(bool defaulted)
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        _ = builder.Services.AddPortia().ConfigureJson(options => options.TypeInfoResolver = ImmutableExampleJsonContext
            .Default
            .WithAddedModifier(info =>
            {
                if (info.Type == typeof(ImmutableExampleRequest) || info.Type == typeof(DefaultedImmutableRequest))
                    foreach (var property in info.Properties)
                        property.Name = "wire_" + property.Name;
            })).AddHttp().AddRequestHandler<ImmutableExampleHandler>().AddRequestHandler<DefaultedImmutableHandler>();
        await using var app = builder.Build();
        _ = app.MapPortiaOpenApi();

        if (defaulted)
            _ = app.MapPortiaPost<DefaultedImmutableRequest, string>("/immutable", configuration => configuration
                .RequestExample("sample", new DefaultedImmutableRequest("example")));
        else
            _ = app.MapPortiaPost<ImmutableExampleRequest, string>("/immutable", configuration => configuration
                .RequestExample("sample", new ImmutableExampleRequest("example", null)));

        await app.StartAsync(TestContext.Current.CancellationToken);

        // Act
        var document = JsonNode.Parse(await app.GetTestClient().GetStringAsync("/openapi/v1.json", TestContext.Current.CancellationToken))!;

        // Assert
        var value =
            document["paths"]!["/immutable"]!["post"]!["requestBody"]!["content"]!["application/json"]!["examples"]!
                ["sample"]!["value"]!;
        Assert.Equal("example", value["wire_text"]!.GetValue<string>());
        Assert.Null(value["wire_note"]);
    }
}

sealed class DefaultedImmutableRequest(string? text = null) : IRequest<string>, ICallable
{
    [System.ComponentModel.DataAnnotations.Required]
    public string? Text { get; } = text;
}

sealed class DefaultedImmutableHandler : IRequestHandler<DefaultedImmutableRequest, string>
{
    public ValueTask<Result<string>> HandleAsync(
        IRequestContext<DefaultedImmutableRequest> context,
        CancellationToken ct
    ) =>
        ValueTask.FromResult(Result<string>.Success(context.Request.Text!));
}

sealed class ImmutableExampleRequest(string text, string? note, int count = 7) : IRequest<string>, ICallable
{
    public string? Note { get; } = note;
    public int Count { get; } = count;
    public string Text { get; } = text;
}

sealed class ImmutableExampleHandler : IRequestHandler<ImmutableExampleRequest, string>
{
    public ValueTask<Result<string>>
        HandleAsync(IRequestContext<ImmutableExampleRequest> context, CancellationToken ct) =>
        ValueTask.FromResult(Result<string>.Success(context.Request.Text));
}

[PortiaJsonContext]
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(ImmutableExampleRequest))]
[JsonSerializable(typeof(DefaultedImmutableRequest))]
[JsonSerializable(typeof(string))]
sealed partial class ImmutableExampleJsonContext : JsonSerializerContext;
