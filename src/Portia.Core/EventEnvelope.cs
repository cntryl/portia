using System.Text.Json.Nodes;

namespace Cntryl.Portia;

sealed record EventEnvelope(string Name, int Version, JsonObject Metadata, JsonObject Payload);
