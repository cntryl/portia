namespace Cntryl.Portia.Consumer;

public sealed record Deposited(int Amount) : DomainEvent;

public sealed record Declined(string Reason) : DomainEvent;
