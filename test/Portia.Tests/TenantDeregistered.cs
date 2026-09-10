namespace Cntryl.Portia;

[Discriminator("test.tenant.deregistered")]
sealed record TenantDeregistered(string TenantId) : DomainEvent;
