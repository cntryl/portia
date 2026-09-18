using System.Text.Json.Serialization;

namespace Cntryl.Portia;

[PortiaJsonContext]
[JsonSerializable(typeof(StdioGreeting))]
[JsonSerializable(typeof(string))]
sealed partial class StdioJsonContext : JsonSerializerContext;
