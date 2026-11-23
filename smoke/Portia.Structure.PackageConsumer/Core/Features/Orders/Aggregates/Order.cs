using Cntryl.Portia.Features.Orders.Aggregates.Entities;
using Cntryl.Portia.Features.Orders.Events;

namespace Cntryl.Portia.Features.Orders.Aggregates;

/// <summary>Owns line state and enforces an order-wide total quantity limit.</summary>
public sealed class Order : Aggregate
{
    readonly Dictionary<Uuid, OrderLine> _lines = [];

    /// <summary>Creates an order with the supplied stable identity.</summary>
    /// <param name="id">The order identity.</param>
    public Order(Uuid id)
        : base(id, new EventStreamAddress("shop", "orders", id.ToString()))
    {
        On<OrderLineQuantityChanged>(Apply);
    }

    /// <summary>Creates an order with a new stable identity.</summary>
    public Order()
        : this(Uuid.CreateVersion4())
    {
    }

    /// <summary>Gets the lines currently belonging to this order.</summary>
    public IReadOnlyCollection<OrderLine> Lines => _lines.Values;

    /// <summary>Gets the total quantity across all lines.</summary>
    public int TotalQuantity => _lines.Values.Sum(line => line.Quantity);

    /// <summary>Changes one child quantity if both its own and the order-wide rules permit it.</summary>
    /// <param name="lineId">The stable child identity.</param>
    /// <param name="quantity">The proposed quantity.</param>
    /// <returns>The operation outcome.</returns>
    public Result ChangeLineQuantity(Uuid lineId, int quantity)
    {
        if (lineId == Uuid.Empty)
        {
            return RequestError.Validation("Line ID is required.");
        }

        var existing = _lines.GetValueOrDefault(lineId);

        if (existing?.Quantity == quantity)
        {
            return Result.Success;
        }

        var candidate = existing ?? new OrderLine(lineId);

        if (!candidate.CanChangeQuantity(quantity))
        {
            return RequestError.Validation("Line quantity must be between 1 and 10.");
        }

        var otherQuantity = TotalQuantity - (existing?.Quantity ?? 0);

        if (otherQuantity + quantity > 12)
        {
            return RequestError.Validation("Order quantity cannot exceed 12.");
        }

        RaiseEvent(new OrderLineQuantityChanged(lineId, quantity));
        return Result.Success;
    }

    void Apply(OrderLineQuantityChanged ev)
    {
        if (!_lines.TryGetValue(ev.LineId, out var line))
        {
            _lines.Add(ev.LineId, line = new OrderLine(ev.LineId));
        }

        line.ApplyQuantity(ev.Quantity);
    }
}
