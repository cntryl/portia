using System.Text.Json.Nodes;

namespace Cntryl.Portia;

sealed class WidgetNamedToRenamedUpcaster : IJsonDomainEventUpcaster
{
    public string EventName => "WidgetNamed";

    public int FromVersion => 1;

    public JsonObject Upcast(JsonObject payload) => new()
    {
        ["display_name"] = payload["name"]?.GetValue<string>()
    };
}
