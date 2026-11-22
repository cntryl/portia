namespace Cntryl.Portia.Features.Orders.Aggregates.Entities;

/// <summary>A child entity that owns its line identity and quantity rules.</summary>
public sealed class OrderLine(Uuid id)
{
    /// <summary>Gets the stable line identity.</summary>
    public Uuid Id { get; } = id != Uuid.Empty ? id : throw new ArgumentException("Line ID is required.", nameof(id));

    /// <summary>Gets the current quantity in this line.</summary>
    public int Quantity { get; private set; }

    /// <summary>Checks whether this line can propose the requested quantity.</summary>
    public bool CanChangeQuantity(int quantity) => Quantity >= 0 && quantity is > 0 and <= 10;

    /// <summary>Applies an accepted quantity fact during command handling or stream replay.</summary>
    /// <param name="quantity">The accepted quantity.</param>
    public void ApplyQuantity(int quantity)
    {
        if (!CanChangeQuantity(quantity))
            throw new ArgumentOutOfRangeException(nameof(quantity));
        Quantity = quantity;
    }
}
