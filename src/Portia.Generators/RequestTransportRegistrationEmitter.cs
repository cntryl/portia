using System.Text;

namespace Cntryl.Portia;

/// <summary>
/// Emits the construction of one <c>RequestTransportRegistration</c>. Shared by the request and
/// handler registration generators so a request's transport metadata is written in exactly one
/// place, whether it reaches the container through its own generated method or through the
/// generated method of the handler that handles it.
/// </summary>
static class RequestTransportRegistrationEmitter
{
    public static void AppendConstruction(StringBuilder source, RequestTransportComponent request)
    {
        _ = source.Append("new global::Cntryl.Portia.RequestTransportRegistration(typeof(")
            .Append(request.TypeName)
            .Append("), ")
            .Append(RequestTransportDiscovery.FormatTransports(request.Transports))
            .Append(", new global::Cntryl.Portia.RequestRouteAttribute(")
            .Append(RequestTransportDiscovery.FormatStringLiteral(request.Realm))
            .Append(", ")
            .Append(RequestTransportDiscovery.FormatStringLiteral(request.Area))
            .Append(", ")
            .Append(RequestTransportDiscovery.FormatStringLiteral(request.Resource))
            .Append(", ")
            .Append(RequestTransportDiscovery.FormatStringLiteral(request.Operation))
            .Append("), new global::Cntryl.Portia.DiscriminatorAttribute(")
            .Append(RequestTransportDiscovery.FormatStringLiteral(request.DiscriminatorName))
            .Append(", ")
            .Append(request.DiscriminatorVersion)
            .Append(')');

        if (request.Transports.HasFlag(RequestTransports.Callable))
        {
            _ = source.Append(", static (registrar, ct) => registrar.RegisterAsync<")
                .Append(request.TypeName)
                .Append(request.ResultType is null ? string.Empty : $", {request.ResultType}")
                .Append(">(ct)");
        }

        _ = source.Append(')');
    }
}
