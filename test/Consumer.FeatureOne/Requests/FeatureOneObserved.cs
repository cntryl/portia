namespace Cntryl.Portia.Consumer;

[Discriminator("FeatureOneObserved")]
public sealed record FeatureOneObserved(int Value) : DomainEvent;
