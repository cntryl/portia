namespace Cntryl.Portia.Features.Orders.Events;

/// <summary>Records the root's authoritative change to one order line.</summary>
/// <param name="LineId">The stable identity of the child entity.</param>
/// <param name="Quantity">The accepted child quantity.</param>
[Discriminator("orders.order_line_quantity_changed", 1)]
public sealed record OrderLineQuantityChanged(Uuid LineId, int Quantity) : DomainEvent;
