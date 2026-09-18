namespace Cntryl.Portia.Consumer;

[Discriminator("FeatureTwoObserved")]
public sealed record FeatureTwoObserved(int Value) : DomainEvent;
