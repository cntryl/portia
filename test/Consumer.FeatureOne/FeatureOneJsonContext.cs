using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cntryl.Portia.Consumer;

[PortiaJsonContext]
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(FeatureOneRequest))]
[JsonSerializable(typeof(FeatureOneObserved))]
[JsonSerializable(typeof(DepositAccount))]
[JsonSerializable(typeof(Deposited))]
[JsonSerializable(typeof(Declined))]
[JsonSerializable(typeof(string))]
sealed partial class FeatureOneJsonContext : JsonSerializerContext;
