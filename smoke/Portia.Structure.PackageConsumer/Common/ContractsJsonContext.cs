using System.Text.Json.Serialization;

namespace Cntryl.Portia;

/// <summary>Serializer contract for Common's event stream.</summary>
[PortiaJsonContext]
[JsonSerializable(typeof(Features.Orders.Events.OrderLineQuantityChanged))]
public sealed partial class ContractsJsonContext : JsonSerializerContext;
