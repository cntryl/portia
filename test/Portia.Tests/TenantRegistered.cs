namespace Cntryl.Portia;

[Discriminator("test.tenant.registered")]
sealed record TenantRegistered(string TenantId) : DomainEvent;
