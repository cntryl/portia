using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;

namespace Cntryl.Portia.ReflectionDisabled;

public sealed class ReflectionDisabledConsumerTests
{
    [Fact]
    public void RequestAndEventEnvelopesRoundTripWithoutReflection()
    {
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
        var options = new JsonSerializerOptions(ReflectionDisabledJsonContext.Default.Options);
        var registration = new RequestTransportRegistration(typeof(CreateGreeting), RequestTransports.Callable,
            new RequestRouteAttribute("public", "greetings", "messages", "create"),
            new DiscriminatorAttribute("greetings.create"));
        var requests = new JsonRequestSerializer([registration], options);
        var bytes = requests.Serialize(new CreateGreeting("Portia"), null, RequestMetadata.Create(), null);
        Assert.Equal(new CreateGreeting("Portia"), requests.DeserializeEnvelope(bytes).Request);

        var events = new JsonDomainEventSerializer(
            new DomainEventTypeCatalog().Register<GreetingCreated>(1, "greetings.created"), null, options);
        var created = new GreetingCreated("Portia");
        created.AttachMetadata(new DomainEventMetadata(Uuid.CreateVersion4(), Uuid.CreateVersion4(), 1, DateTimeOffset.UtcNow));
        var persisted = events.Serialize(created);
        _ = Assert.IsType<GreetingCreated>(events.Deserialize(persisted));
    }

    [Fact]
    public async Task GeneratedHttpBindingRunsWithoutReflection()
    {
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        var portia = builder.Services.AddPortia();
        _ = portia.AddRequestHandler<CreateGreetingHandler>();
        await using var app = builder.Build();
        _ = app.MapPortiaPost<CreateGreeting, string>("/greetings");
        await app.StartAsync();

        using var content = new StringContent(
            JsonSerializer.Serialize(new CreateGreeting("Portia"), ReflectionDisabledJsonContext.Default.CreateGreeting),
            Encoding.UTF8, "application/json");
        using var response = await app.GetTestClient().PostAsync("/greetings", content);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"Hello, Portia!\"", await response.Content.ReadAsStringAsync());

        using var invalidContent = new StringContent(
            JsonSerializer.Serialize(new CreateGreeting(string.Empty), ReflectionDisabledJsonContext.Default.CreateGreeting),
            Encoding.UTF8, "application/json");
        using var invalid = await app.GetTestClient().PostAsync("/greetings", invalidContent);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains("A name is required.", await invalid.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}

[RequestRoute("public", "greetings", "messages", "create")]
[Discriminator("greetings.create")]
public sealed record CreateGreeting(string Name) : IRequest<string>, ICallable;

[Discriminator("greetings.created")]
public sealed record GreetingCreated(string Name) : DomainEvent;

public sealed class CreateGreetingHandler : IRequestHandler<CreateGreeting, string>
{
    public ValueTask<Result<string>> HandleAsync(IRequestContext<CreateGreeting> context, CancellationToken ct)
        => ValueTask.FromResult(string.IsNullOrWhiteSpace(context.Request.Name)
            ? Result<string>.Failure(new RequestError(RequestErrorKind.Validation, "A name is required."))
            : Result<string>.Success($"Hello, {context.Request.Name}!"));
}

[PortiaJsonContext]
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(CreateGreeting))]
[JsonSerializable(typeof(GreetingCreated))]
[JsonSerializable(typeof(string))]
sealed partial class ReflectionDisabledJsonContext : JsonSerializerContext;
