using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cntryl.Portia;

sealed record OpenApiContractNode(
    string DisplayName,
    [property: JsonPropertyName("wire-name")]
    string ExplicitName,
    DayOfWeek DayValue,
    HttpMoney CustomValue,
    Uuid TeamId,
    Uuid? ParentTeamId,
    OpenApiContractNode? NextNode = null,
    Uuid? OperatorUserId = null,
    IReadOnlyList<Uuid> CandidateApplicationIds = null!);

[Discriminator("test.openapi.contract")]
[RequestRoute("test", "openapi", "contract", "echo")]
sealed record OpenApiContractRequest(OpenApiContractNode Payload) : IRequest<OpenApiContractNode>, ICallable;

sealed class OpenApiContractHandler : IRequestHandler<OpenApiContractRequest, OpenApiContractNode>
{
    public ValueTask<Result<OpenApiContractNode>> HandleAsync(IRequestContext<OpenApiContractRequest> context,
        CancellationToken ct) =>
        ValueTask.FromResult(Result<OpenApiContractNode>.Success(context.Request.Payload));
}

[Discriminator("test.openapi.stream")]
[RequestRoute("test", "openapi", "contract", "stream")]
sealed record OpenApiContractStream : IStreamRequest<OpenApiContractNode>, ICallable;

sealed class OpenApiContractStreamHandler : IStreamRequestHandler<OpenApiContractStream, OpenApiContractNode>
{
    public async IAsyncEnumerable<OpenApiContractNode> HandleAsync(IRequestContext<OpenApiContractStream> context,
        [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new OpenApiContractNode("stream", "explicit", DayOfWeek.Monday, new HttpMoney("USD", 42),
            Uuid.CreateVersion4(), null);
        await Task.CompletedTask;
    }
}

sealed class OpenApiMoneyConverter : JsonConverter<HttpMoney>
{
    public override HttpMoney Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new("USD", int.Parse(reader.GetString()!, CultureInfo.InvariantCulture));

    public override void Write(Utf8JsonWriter writer, HttpMoney value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Cents.ToString(CultureInfo.InvariantCulture));
}
