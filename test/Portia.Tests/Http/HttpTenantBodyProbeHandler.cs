namespace Cntryl.Portia;

sealed class HttpTenantBodyProbeHandler : IRequestHandler<HttpTenantBodyProbe, string>
{
    public ValueTask<Result<string>> HandleAsync(IRequestContext<HttpTenantBodyProbe> context, CancellationToken ct) =>
        ValueTask.FromResult<Result<string>>($"{context.Request.Tenant}:{context.Request.Name}");
}
