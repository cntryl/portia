namespace Cntryl.Portia.Features.Orders.Commands;

/// <summary>Protects a private order reference for storage.</summary>
[Discriminator("orders.protect_reference", 1)]
public sealed record ProtectOrderReference(Uuid OrderId, byte[] Reference) : IRequest<byte[]>, ISystemRequest;
