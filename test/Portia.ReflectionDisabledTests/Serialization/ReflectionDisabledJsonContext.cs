using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cntryl.Portia.ReflectionDisabled;

[PortiaJsonContext]
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(CreateGreeting))]
[JsonSerializable(typeof(BodylessCreate))]
[JsonSerializable(typeof(NullableCreate))]
[JsonSerializable(typeof(OrdinaryCreate))]
[JsonSerializable(typeof(GreetingCreated))]
[JsonSerializable(typeof(PortiaCreateOutcome))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(PortiaCreateOutcome<string>))]
sealed partial class ReflectionDisabledJsonContext : JsonSerializerContext;
