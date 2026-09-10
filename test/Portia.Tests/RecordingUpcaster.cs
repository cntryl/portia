using System.Text.Json.Nodes;

namespace Cntryl.Portia;

sealed class RecordingUpcaster(string name, int fromVersion) : IJsonDomainEventUpcaster
{
    public bool Executed { get; private set; }
    public string EventName => name;
    public int FromVersion => fromVersion;

    public JsonObject Upcast(JsonObject payload)
    {
        Executed = true;
        return payload;
    }
}
