namespace Cntryl.Portia;

sealed class RequestTransportComponent : IEquatable<RequestTransportComponent>
{
    public RequestTransportComponent(string typeName, IReadOnlyList<string> transports, string realm, string area,
        string resource, string operation, int discriminatorVersion, string discriminatorName, string? resultType)
    {
        TypeName = typeName;
        Transports = transports.ToArray();
        Realm = realm;
        Area = area;
        Resource = resource;
        Operation = operation;
        DiscriminatorVersion = discriminatorVersion;
        DiscriminatorName = discriminatorName;
        ResultType = resultType;
    }

    public string TypeName { get; }
    public IReadOnlyList<string> Transports { get; }
    public string Realm { get; }
    public string Area { get; }
    public string Resource { get; }
    public string Operation { get; }
    public int DiscriminatorVersion { get; }
    public string DiscriminatorName { get; }
    public string? ResultType { get; }
    public DiagnosticLocation Location { get; private init; }

    public bool Equals(RequestTransportComponent? other) => other is not null
                                                            && TypeName == other.TypeName &&
                                                            Transports.SequenceEqual(other.Transports,
                                                                StringComparer.Ordinal)
                                                            && Realm == other.Realm && Area == other.Area &&
                                                            Resource == other.Resource && Operation == other.Operation
                                                            && DiscriminatorVersion == other.DiscriminatorVersion &&
                                                            DiscriminatorName == other.DiscriminatorName
                                                            && ResultType == other.ResultType &&
                                                            Location == other.Location;

    public RequestTransportComponent WithLocation(DiagnosticLocation location) => new(TypeName, Transports, Realm,
            Area, Resource, Operation, DiscriminatorVersion, DiscriminatorName, ResultType)
    { Location = location };

    public override bool Equals(object? obj) => Equals(obj as RequestTransportComponent);

    public override int GetHashCode()
    {
        var hash = StringComparer.Ordinal.GetHashCode(TypeName);
        foreach (var transport in Transports)
            hash = unchecked((hash * 397) ^ StringComparer.Ordinal.GetHashCode(transport));
        hash = unchecked((hash * 397) ^ StringComparer.Ordinal.GetHashCode(Realm));
        hash = unchecked((hash * 397) ^ StringComparer.Ordinal.GetHashCode(Area));
        hash = unchecked((hash * 397) ^ StringComparer.Ordinal.GetHashCode(Resource));
        hash = unchecked((hash * 397) ^ StringComparer.Ordinal.GetHashCode(Operation));
        hash = unchecked((hash * 397) ^ DiscriminatorVersion);
        hash = unchecked((hash * 397) ^ StringComparer.Ordinal.GetHashCode(DiscriminatorName));
        hash = unchecked((hash * 397) ^ (ResultType is null ? 0 : StringComparer.Ordinal.GetHashCode(ResultType)));
        return unchecked((hash * 397) ^ Location.GetHashCode());
    }
}
