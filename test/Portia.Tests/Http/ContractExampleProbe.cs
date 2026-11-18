namespace Cntryl.Portia;

[System.ComponentModel.DisplayName("Create example")]
sealed record ContractExampleRequest(int Id, ContractMetadataModel Payload, int PageSize = 5)
    : IRequest<ContractMetadataModel>, ICallable;

sealed class ContractExampleHandler : IRequestHandler<ContractExampleRequest, ContractMetadataModel>
{
    public ValueTask<Result<ContractMetadataModel>> HandleAsync(IRequestContext<ContractExampleRequest> context,
        CancellationToken ct) =>
        ValueTask.FromResult(Result<ContractMetadataModel>.Success(context.Request.Payload));
}

sealed record ConvertedExampleRequest(
    [property: System.Text.Json.Serialization.JsonConverter(typeof(HttpMemberHexConverter))]
    int Id,
    [property: System.Text.Json.Serialization.JsonConverter(typeof(HttpMemberHexConverter))]
    int Count = 0) : IRequest<string>, ICallable;

sealed class ConvertedExampleHandler : IRequestHandler<ConvertedExampleRequest, string>
{
    public ValueTask<Result<string>>
        HandleAsync(IRequestContext<ConvertedExampleRequest> context, CancellationToken ct) =>
        ValueTask.FromResult(Result<string>.Success($"{context.Request.Id},{context.Request.Count}"));
}

enum ExampleMode
{
    First = 1,
    Second = 2
}

sealed record EnumExampleRequest(ExampleMode Mode) : IRequest<string>, ICallable;

sealed class EnumExampleHandler : IRequestHandler<EnumExampleRequest, string>
{
    public ValueTask<Result<string>> HandleAsync(IRequestContext<EnumExampleRequest> context, CancellationToken ct) =>
        ValueTask.FromResult(Result<string>.Success(context.Request.Mode.ToString()));
}
