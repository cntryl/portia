namespace Cntryl.Portia;

[Discriminator("OrderPlaced", 2)]
sealed record OrderPlacedV2(int AmountCents, string Currency) : DomainEvent;
