namespace Cntryl.Portia;

[Discriminator("WidgetNamed")]
sealed record WidgetNamed(string Name) : DomainEvent;
