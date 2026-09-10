using System.Text.Json;

namespace Cntryl.Portia.Consumer;

static class ConsumerJson
{
    static readonly RequestTransportRegistration[] Registrations =
    [
        Registration<DepositAccount>("consumer", "business", "*", "deposit", "consumer.business.deposit-account"),
        Registration<FeatureOneRequest>("consumer", "modules", "one", "get", "consumer.modules.feature-one"),
        Registration<FeatureTwoRequest>("consumer", "modules", "two", "get", "consumer.modules.feature-two"),
        Registration<ScopeRequest>("consumer", "scopes", "delivery", "run", "consumer.scopes.scope-request"),
        Registration<BrokerExecutionContextTests.Command>("context", "work", "*", "execute", "consumer.context.command")
    ];

    public static JsonSerializerOptions Options() => new(PortiaConsumerJsonContext.Default.Options);

    public static JsonDomainEventSerializer DomainSerializer(DomainEventTypeCatalog catalog,
        IEnumerable<IJsonDomainEventUpcaster>? upcasters = null) => new(catalog, upcasters, Options());

    public static RequestTransportCatalog Catalog() => new(Registrations);

    public static JsonRequestSerializer CreateSerializer() => new(Registrations, Options());

    static RequestTransportRegistration Registration<TRequest>(string realm, string area, string resource,
        string operation, string discriminator) where TRequest : IRequestBase => new(typeof(TRequest),
        RequestTransports.Callable | RequestTransports.Queuable | RequestTransports.Notifiable |
        RequestTransports.Schedulable,
        new RequestRouteAttribute(realm, area, resource, operation), new DiscriminatorAttribute(discriminator));
}
