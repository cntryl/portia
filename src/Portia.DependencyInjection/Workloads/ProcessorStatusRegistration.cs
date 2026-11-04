using System.Text.Json;
using System.Text.Json.Serialization;

[assembly: Cntryl.Portia.PortiaJsonRoot(typeof(Cntryl.Portia.GetProcessorStatus),
    typeof(Cntryl.Portia.ProcessorStatusJsonContext), typeof(Cntryl.Portia.ProcessorStatusJsonContextFactory))]
[assembly: Cntryl.Portia.PortiaJsonRoot(typeof(Cntryl.Portia.ProcessorStatus),
    typeof(Cntryl.Portia.ProcessorStatusJsonContext), typeof(Cntryl.Portia.ProcessorStatusJsonContextFactory))]

namespace Cntryl.Portia;

static class ProcessorStatusRegistration
{
    static readonly object Identity = new();

    internal static void Add(PortiaBuilder builder) => builder.AddGeneratedRegistrations(Identity, static application =>
    {
        _ = application.AddGeneratedHandler(
            new RequestRegistration<GetProcessorStatus, ProcessorStatusHandler, ProcessorStatus>());
        _ = application.AddGeneratedAuthorizer(
            new RequestAuthorizerRegistration<GetProcessorStatus, ProcessorStatusAuthorizer>(AuthorizationStage.Principal));
        _ = application.AddGeneratedRequest(new RequestTransportRegistration(typeof(GetProcessorStatus),
            [RequestTransportId.Callable], new RequestRouteAttribute("*", "*", "*", "status"),
            new DiscriminatorAttribute("portia.processor_status", 1),
            static (registrar, ct) => registrar.RegisterAsync<GetProcessorStatus, ProcessorStatus>(ct),
            typeof(ProcessorStatus)));
        _ = application.AddGeneratedJsonContext(static options => new ProcessorStatusJsonContext(options));
    });
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(GetProcessorStatus))]
[JsonSerializable(typeof(ProcessorStatus))]
sealed partial class ProcessorStatusJsonContext : JsonSerializerContext;
