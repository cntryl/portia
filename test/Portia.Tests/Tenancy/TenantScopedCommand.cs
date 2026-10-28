namespace Cntryl.Portia;

[Discriminator("test.tenancy.scoped-command")]
sealed record TenantScopedCommand(TenantId Tenant) : IRequest, ITenantRequest;

sealed class TenantScopedCommandHandler : IRequestHandler<TenantScopedCommand>
{
    public ValueTask<Result> HandleAsync(IRequestContext<TenantScopedCommand> context, CancellationToken ct) =>
        ValueTask.FromResult(Result.Success);
}
