namespace Cntryl.Portia;

[Discriminator("WidgetNamed", 2)]
sealed record WidgetRenamed(string DisplayName) : DomainEvent;
