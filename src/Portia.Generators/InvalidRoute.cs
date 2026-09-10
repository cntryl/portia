using Microsoft.CodeAnalysis;

namespace Cntryl.Portia;

sealed class InvalidRoute(string typeName, string segment, Location location)
{
    public string TypeName { get; } = typeName;

    public string Segment { get; } = segment;

    public Location Location { get; } = location;
}
