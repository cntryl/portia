using Microsoft.CodeAnalysis;

namespace Cntryl.Portia;

sealed class InvalidDiscriminator(string typeName, Location location)
{
    public string TypeName { get; } = typeName;

    public Location Location { get; } = location;
}
