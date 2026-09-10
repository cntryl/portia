namespace Cntryl.Portia;

[Discriminator("Gadget", 2)]
sealed record GadgetRenamed(string DisplayName) : DomainEvent;
