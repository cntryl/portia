using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Cntryl.Portia.DataProtection;
using Cntryl.Portia.Features.Orders.Commands;

namespace Cntryl.Portia.Features.Orders.Handlers;

/// <summary>Protects a private reference under its owning order.</summary>
public sealed class ProtectOrderReferenceHandler(IPayloadProtector protector) : IRequestHandler<ProtectOrderReference, byte[]>
{
    /// <inheritdoc />
    public async ValueTask<Result<byte[]>> HandleAsync(IRequestContext<ProtectOrderReference> context, CancellationToken ct)
    {
        var request = context.Request;
        var associatedData = Encoding.UTF8.GetBytes(request.OrderId.ToString());
        return await protector.ProtectAsync(request.Reference, "orders.private-reference", associatedData, ct)
            .ConfigureAwait(false);
    }
}
