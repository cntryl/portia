using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Cntryl.Portia;

readonly record struct InterceptableLocationModel(int Version, string Data)
{
    public static InterceptableLocationModel From(InterceptableLocation location) =>
        new(location.Version, location.Data);
}
