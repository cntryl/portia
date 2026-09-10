using System.Globalization;
using System.Text.Json;

namespace Cntryl.Portia;

static class TestJson
{
    public static JsonSerializerOptions Options() => new(PortiaTestJsonContext.Default.Options);

    public static JsonDomainEventSerializer DomainSerializer(DomainEventTypeCatalog catalog,
        IEnumerable<IJsonDomainEventUpcaster>? upcasters = null) => new(catalog, upcasters, Options());

    public static JsonRequestSerializer Serializer(params Type[] types) => new(
        types.Select((type, index) => new RequestTransportRegistration(type, RequestTransports.Callable,
            Route(type, index),
            new DiscriminatorAttribute(Name(type)))),
        Options());

    static RequestRouteAttribute Route(Type type, int index) => type == typeof(UniversalAction)
        ? new RequestRouteAttribute("test", "shared", "action", "run")
        : type == typeof(RpcGetValue)
            ? new RequestRouteAttribute("test", "rpc", "value", "get")
            : type == typeof(RpcChangeValue)
                ? new RequestRouteAttribute("test", "rpc", "value", "change")
                : type == typeof(NoWorkerRegisteredPing)
                    ? new RequestRouteAttribute("portia-integration", "rpc", "no-worker-registered", "ping")
                    : new RequestRouteAttribute("test", "test", index.ToString(CultureInfo.InvariantCulture), "run");

    static string Name(Type type) => type == typeof(UniversalAction) ? "test.shared.universal-action"
        : type == typeof(RpcGetValue) ? "test.rpc.get-value"
        : type == typeof(RpcChangeValue) ? "test.rpc.change-value"
        : type == typeof(NoWorkerRegisteredPing) ? "test.rpc.no-worker-registered"
        : "test." + type.Name;
}
