namespace Cntryl.Portia;

sealed record RequestTransportComponent(
    string TypeName,
    RequestTransports Transports,
    string Realm,
    string Area,
    string Resource,
    string Operation,
    int DiscriminatorVersion,
    string DiscriminatorName,
    string? ResultType)
{
    public DiagnosticLocation Location { get; init; }
}
