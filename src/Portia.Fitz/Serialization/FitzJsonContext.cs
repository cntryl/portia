using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cntryl.Portia;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(RequestMetadata))]
[JsonSerializable(typeof(RequestError))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(FitzScheduledRequestEnvelope))]
[JsonSerializable(typeof(FitzTenantDirectorySnapshot))]
sealed partial class FitzJsonContext : JsonSerializerContext;
