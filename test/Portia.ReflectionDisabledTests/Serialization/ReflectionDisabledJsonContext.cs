using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cntryl.Portia.ReflectionDisabled;

[PortiaJsonContext]
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(CreateGreeting))]
[JsonSerializable(typeof(GreetingCreated))]
[JsonSerializable(typeof(string))]
sealed partial class ReflectionDisabledJsonContext : JsonSerializerContext;
