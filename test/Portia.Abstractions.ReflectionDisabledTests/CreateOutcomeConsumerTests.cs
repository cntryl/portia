using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Cntryl.Portia.AbstractionsConsumer;

public sealed class CreateAccount(string name) : IRequest<PortiaCreateOutcome<Account>>
{
    public string Name { get; } = name;
}

public sealed record Account(string AccountId, string Name);

public sealed class CreateAccountHandler : IRequestHandler<CreateAccount, PortiaCreateOutcome<Account>>
{
    public ValueTask<Result<PortiaCreateOutcome<Account>>> HandleAsync(
        IRequestContext<CreateAccount> context, CancellationToken ct)
    {
        var outcome = PortiaCreateOutcome<Account>.Created(new Account("server-id", context.Request.Name));
        return ValueTask.FromResult(Result<PortiaCreateOutcome<Account>>.Success(outcome));
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(CreateAccount))]
[JsonSerializable(typeof(PortiaCreateOutcome))]
[JsonSerializable(typeof(PortiaCreateOutcome<Account>))]
sealed partial class CreateAccountJsonContext : JsonSerializerContext;

public sealed class CreateOutcomeConsumerTests
{
    [Fact]
    public void ShouldSerializeCreateOutcomeFromAbstractionsGivenReflectionDisabledConsumer()
    {
        // Arrange
        var expected = PortiaCreateOutcome<Account>.Created(new Account("server-id", "Portia"));

        // Act
        var typeInfo = (JsonTypeInfo<PortiaCreateOutcome<Account>>)
            CreateAccountJsonContext.Default.GetTypeInfo(typeof(PortiaCreateOutcome<Account>))!;
        var json = JsonSerializer.Serialize(expected, typeInfo);
        var actual = JsonSerializer.Deserialize(json, typeInfo);

        // Assert
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
        Assert.Equal(typeof(IRequest<>).Assembly, typeof(PortiaCreateOutcome<>).Assembly);
        Assert.NotNull(actual);
        Assert.Equal(PortiaCreateOutcomeKind.Created, actual.Kind);
        Assert.Equal(new Account("server-id", "Portia"), actual.Value);
    }

    [Fact]
    public void ShouldSerializePayloadlessCreateOutcomeGivenAbstractionsOnlyReference()
    {
        // Arrange
        var expected = PortiaCreateOutcome.Repeated();

        // Act
        var typeInfo = (JsonTypeInfo<PortiaCreateOutcome>)
            CreateAccountJsonContext.Default.GetTypeInfo(typeof(PortiaCreateOutcome))!;
        var json = JsonSerializer.Serialize(expected, typeInfo);
        var actual = JsonSerializer.Deserialize(json, typeInfo);

        // Assert
        Assert.NotNull(actual);
        Assert.Equal(PortiaCreateOutcomeKind.Repeated, actual.Kind);
    }
}
