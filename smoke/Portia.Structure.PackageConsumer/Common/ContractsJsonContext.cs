using System.Text.Json.Serialization;

namespace Cntryl.Portia;

/// <summary>Serializer contract for Common's event stream.</summary>
[PortiaJsonContext]
[JsonSerializable(typeof(Features.Orders.Events.OrderLineQuantityChanged))]
[JsonSerializable(typeof(Features.Orders.Commands.ProtectOrderReference))]
[JsonSerializable(typeof(byte[]))]
public sealed partial class ContractsJsonContext : JsonSerializerContext;
