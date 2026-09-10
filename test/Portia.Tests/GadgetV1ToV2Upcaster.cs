using System.Text.Json.Nodes;

namespace Cntryl.Portia;

sealed class GadgetV1ToV2Upcaster : IJsonDomainEventUpcaster
{
    public string EventName => "Gadget";

    public int FromVersion => 1;

    public JsonObject Upcast(JsonObject payload) => new()
    {
        ["display_name"] = payload["name"]?.GetValue<string>()
    };
}
