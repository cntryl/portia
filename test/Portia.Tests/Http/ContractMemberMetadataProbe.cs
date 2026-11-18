using System.ComponentModel.DataAnnotations;

namespace Cntryl.Portia;

sealed record ContractFirstTitleRequest(
    [property: Display(Name = "First payload")]
    ContractMetadataModel Payload) : IRequest<ContractMetadataModel>, ICallable;

sealed record ContractSecondTitleRequest(
    [property: Display(Name = "Second payload")]
    ContractMetadataModel Payload) : IRequest<ContractMetadataModel>, ICallable;

sealed class ContractMemberTitleHandler : IRequestHandler<ContractFirstTitleRequest, ContractMetadataModel>,
    IRequestHandler<ContractSecondTitleRequest, ContractMetadataModel>
{
    public ValueTask<Result<ContractMetadataModel>> HandleAsync(IRequestContext<ContractFirstTitleRequest> context,
        CancellationToken ct) =>
        ValueTask.FromResult(Result<ContractMetadataModel>.Success(context.Request.Payload));

    public ValueTask<Result<ContractMetadataModel>> HandleAsync(IRequestContext<ContractSecondTitleRequest> context,
        CancellationToken ct) =>
        ValueTask.FromResult(Result<ContractMetadataModel>.Success(context.Request.Payload));
}

sealed class ContractLiteralDefaultModel
{
    [Required] public string Name { get; init; } = "fallback";
}

sealed record ContractLiteralDefaultRequest(ContractLiteralDefaultModel Payload) : IRequest<string>, ICallable;

sealed class ContractLiteralDefaultHandler : IRequestHandler<ContractLiteralDefaultRequest, string>
{
    public ValueTask<Result<string>> HandleAsync(IRequestContext<ContractLiteralDefaultRequest> context,
        CancellationToken ct) =>
        ValueTask.FromResult(Result<string>.Success(context.Request.Payload.Name));
}

sealed record ContractQueryRulesRequest(
    [property: Required] string? Name = null,
    [property: System.Text.Json.Serialization.JsonRequired]
    string? Marker = null) : IRequest<string>, ICallable;

sealed class ContractQueryRulesHandler : IRequestHandler<ContractQueryRulesRequest, string>
{
    public ValueTask<Result<string>> HandleAsync(IRequestContext<ContractQueryRulesRequest> context,
        CancellationToken ct) =>
        ValueTask.FromResult(Result<string>.Success(context.Request.Name!));
}

sealed record ContractIgnoredInputRequest(
    [property:
        System.Text.Json.Serialization.JsonIgnore(Condition =
            System.Text.Json.Serialization.JsonIgnoreCondition.WhenReading)]
    string? ServerName = "fixed") : IRequest<string>, ICallable;

sealed class ContractIgnoredInputHandler : IRequestHandler<ContractIgnoredInputRequest, string>
{
    public ValueTask<Result<string>> HandleAsync(IRequestContext<ContractIgnoredInputRequest> context,
        CancellationToken ct) =>
        ValueTask.FromResult(Result<string>.Success(context.Request.ServerName!));
}
