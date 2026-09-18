using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cntryl.Portia.Consumer;

[PortiaJsonContext]
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(FeatureOneRequest))]
[JsonSerializable(typeof(FeatureOneObserved))]
[JsonSerializable(typeof(NestedScopeRequest))]
[JsonSerializable(typeof(ScopeRequest))]
[JsonSerializable(typeof(DepositAccount))]
[JsonSerializable(typeof(Deposited))]
[JsonSerializable(typeof(Declined))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
sealed partial class FeatureOneJsonContext : JsonSerializerContext;
