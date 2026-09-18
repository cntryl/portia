namespace Cntryl.Portia;

[Discriminator("test.value.audited")]
sealed record ValueAudited(string Reason) : DomainEvent;
