namespace Cntryl.Portia;

[Discriminator("OrderPlaced")]
sealed record OrderPlacedV1(int AmountCents) : DomainEvent;
