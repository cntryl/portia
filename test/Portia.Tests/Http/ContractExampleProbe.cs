namespace Cntryl.Portia;

[System.ComponentModel.DisplayName("Create example")]
sealed record ContractExampleRequest(int Id, ContractMetadataModel Payload, int PageSize = 5) : IRequest<ContractMetadataModel>, ICallable;

sealed class ContractExampleHandler : IRequestHandler<ContractExampleRequest, ContractMetadataModel>
{
    public ValueTask<Result<ContractMetadataModel>> HandleAsync(IRequestContext<ContractExampleRequest> context, CancellationToken ct) =>
        ValueTask.FromResult(Result<ContractMetadataModel>.Success(context.Request.Payload));
}
