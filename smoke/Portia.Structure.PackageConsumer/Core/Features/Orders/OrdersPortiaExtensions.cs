using Cntryl.Portia.Features.Orders.Handlers;

namespace Cntryl.Portia.Features.Orders;

/// <summary>Registers the order feature's request handlers.</summary>
public static class OrdersPortiaExtensions
{
    /// <summary>Registers the private order reference handler.</summary>
    public static PortiaBuilder AddOrders(this PortiaBuilder builder) =>
        builder.AddRequestHandler<ProtectOrderReferenceHandler>();
}
