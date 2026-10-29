namespace Cntryl.Portia;

[RequestRoute("*", "http-binding-tests", "tenants", "probe")]
[Discriminator("test.http.tenants.probe")]
sealed record HttpTenantProbe(TenantId Tenant, string Name) : IRequest<string>, ICallable;
