using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Cntryl.Portia;

sealed record ContractMetadataModel(
    [property: Required, StringLength(12, MinimumLength = 2)]
    string? Name,
    [property: Range(1D, 10D, MinimumIsExclusive = true)]
    double? Limit,
    [property: MinLength(1), MaxLength(3)] string[] Items,
    [property: RegularExpression("[A-Z]{2}")]
    string? Code,
    [property: EmailAddress] string? Email,
    [property: Url] string? Website,
    [property: DefaultValue(5)] int PageSize = 5)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenReading)]
    public string ServerValue { get; init; } = "server";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
    public string? Secret { get; init; }
}

sealed record ContractMetadataRequest : IRequest<ContractMetadataModel>, ICallable;

sealed class ContractMetadataHandler : IRequestHandler<ContractMetadataRequest, ContractMetadataModel>
{
    public ValueTask<Result<ContractMetadataModel>> HandleAsync(IRequestContext<ContractMetadataRequest> context,
        CancellationToken ct) =>
        ValueTask.FromResult(Result<ContractMetadataModel>.Success(new("Title", 5, ["one"], "AB", "hi@example.com",
            "https://example.com")));
}

sealed record ContractMetadataInputRequest(ContractMetadataModel Payload) : IRequest<ContractMetadataModel>, ICallable;

sealed class ContractMetadataInputHandler : IRequestHandler<ContractMetadataInputRequest, ContractMetadataModel>
{
    public ValueTask<Result<ContractMetadataModel>> HandleAsync(IRequestContext<ContractMetadataInputRequest> context,
        CancellationToken ct) =>
        ValueTask.FromResult(Result<ContractMetadataModel>.Success(context.Request.Payload));
}
