using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Cntryl.Portia;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(EventEnvelope))]
[JsonSerializable(typeof(DomainEventMetadata))]
[JsonSerializable(typeof(JsonObject))]
sealed partial class PortiaCoreJsonContext : JsonSerializerContext;
