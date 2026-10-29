namespace Cntryl.Portia;

sealed class HttpTenantProbeHandler : IRequestHandler<HttpTenantProbe, string>
{
    public ValueTask<Result<string>> HandleAsync(IRequestContext<HttpTenantProbe> context, CancellationToken ct) =>
        ValueTask.FromResult<Result<string>>($"{context.Request.Tenant}:{context.Request.Name}");
}
