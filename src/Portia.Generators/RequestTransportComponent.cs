namespace Cntryl.Portia;

sealed class RequestTransportComponent(
    string typeName,
    RequestTransports transports,
    string realm,
    string area,
    string resource,
    string operation,
    int discriminatorVersion,
    string discriminatorName,
    string? resultType)
{
    public string TypeName { get; } = typeName;

    public RequestTransports Transports { get; } = transports;

    public string Realm { get; } = realm;

    public string Area { get; } = area;

    public string Resource { get; } = resource;

    public string Operation { get; } = operation;

    public int DiscriminatorVersion { get; } = discriminatorVersion;

    public string DiscriminatorName { get; } = discriminatorName;

    public string? ResultType { get; } = resultType;
}
