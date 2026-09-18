using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cntryl.Portia.Consumer;

[PortiaJsonContext]
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(Deposited))]
[JsonSerializable(typeof(Declined))]
[JsonSerializable(typeof(DepositAccount))]
[JsonSerializable(typeof(FeatureOneRequest))]
[JsonSerializable(typeof(FeatureTwoRequest))]
[JsonSerializable(typeof(FeatureOneObserved))]
[JsonSerializable(typeof(FeatureTwoObserved))]
[JsonSerializable(typeof(ScopeRequest))]
[JsonSerializable(typeof(NestedScopeRequest))]
[JsonSerializable(typeof(BrokerExecutionContextTests.Command))]
[JsonSerializable(typeof(string))]
sealed partial class PortiaConsumerJsonContext : JsonSerializerContext;
