namespace Cntryl.Portia;

[RequestRoute("*", "http-binding-tests", "tenants", "body-probe")]
[Discriminator("test.http.tenants.body-probe")]
sealed record HttpTenantBodyProbe(TenantId Tenant, string Name) : IRequest<string>, ICallable;
