namespace Cntryl.Portia;

[RequiresPermission("orders:{OrderId}:read")]
sealed record GetOrder(int OrderId) : IRequest<int>;
