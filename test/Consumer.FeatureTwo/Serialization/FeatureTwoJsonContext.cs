using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cntryl.Portia.Consumer;

[PortiaJsonContext]
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(FeatureTwoRequest))]
[JsonSerializable(typeof(FeatureTwoObserved))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
sealed partial class FeatureTwoJsonContext : JsonSerializerContext;
