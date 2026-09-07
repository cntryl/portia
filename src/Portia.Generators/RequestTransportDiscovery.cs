using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cntryl.Portia;

/// <summary>
/// Discovers every routed, transport-marked request in a compilation — shared by every generator
/// that needs it (<see cref="PortiaServiceRegistrationGenerator" /> for the DI transport registry,
/// generated typed RPC descriptors for RPC worker registration) so the discovery
/// rules live in exactly one place, not duplicated per generator.
/// </summary>
static class RequestTransportDiscovery
{
    const string RequestRouteAttributeMetadataName = "Cntryl.Portia.RequestRouteAttribute";
    const string CallableMetadataName = "Cntryl.Portia.ICallable";
    const string QueuableMetadataName = "Cntryl.Portia.IQueuable";
    const string NotifiableMetadataName = "Cntryl.Portia.INotifiable";
    const string SchedulableMetadataName = "Cntryl.Portia.ISchedulable";
    const string RequestWithResultMetadataName = "IRequest`1";
    const string StreamRequestMetadataName = "IStreamRequest`1";

    public static bool IsCandidate(SyntaxNode node) => node is TypeDeclarationSyntax;

    public static RequestTransportComponent? GetRequestTransportComponent(GeneratorSyntaxContext context)
    {
        var declaration = (TypeDeclarationSyntax)context.Node;
        return context.SemanticModel.GetDeclaredSymbol(declaration) is INamedTypeSymbol symbol
            ? GetRequestTransportComponent(symbol)
            : null;
    }

    public static RequestTransportComponent? GetRequestTransportComponent(ITypeSymbol? type)
    {
        if (type is not INamedTypeSymbol symbol || symbol.IsAbstract)
            return null;

        // A streamed request has no RPC, queue, notice, or schedule transport at all today —
        // only MapPortiaGetStream/MapPortiaGetSse exist for it, wired directly by the HTTP
        // binding generator without going through this transport table. ICallable on a stream
        // request means "callable over HTTP", not "callable over Fitz RPC" the way it does for
        // IRequest/IRequest<T> — so it's excluded here rather than mis-registered as RPC-eligible.
        if (symbol.AllInterfaces.Any(iface =>
            iface.OriginalDefinition.ContainingNamespace.ToDisplayString() == "Cntryl.Portia"
            && iface.OriginalDefinition.MetadataName == StreamRequestMetadataName))
        {
            return null;
        }

        var routeAttribute = symbol.GetAttributes()
            .FirstOrDefault(attribute => attribute.AttributeClass?.ToDisplayString() == RequestRouteAttributeMetadataName);

        if (routeAttribute is null || routeAttribute.ConstructorArguments.Length != 4)
            return null;

        var transports = RequestTransports.None;

        if (ImplementsInterface(symbol, CallableMetadataName))
            transports |= RequestTransports.Callable;

        if (ImplementsInterface(symbol, QueuableMetadataName))
            transports |= RequestTransports.Queuable;

        if (ImplementsInterface(symbol, NotifiableMetadataName))
            transports |= RequestTransports.Notifiable;

        if (ImplementsInterface(symbol, SchedulableMetadataName))
            transports |= RequestTransports.Schedulable;

        if (transports == RequestTransports.None)
            return null;

        const string wildcard = "*";

        var resultTypeInterface = symbol.AllInterfaces.FirstOrDefault(iface =>
            iface.OriginalDefinition.ContainingNamespace.ToDisplayString() == "Cntryl.Portia"
            && iface.OriginalDefinition.MetadataName == RequestWithResultMetadataName);

        return new RequestTransportComponent(
            symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            transports,
            (string?)routeAttribute.ConstructorArguments[0].Value ?? wildcard,
            (string?)routeAttribute.ConstructorArguments[1].Value ?? wildcard,
            (string?)routeAttribute.ConstructorArguments[2].Value ?? wildcard,
            (string?)routeAttribute.ConstructorArguments[3].Value ?? wildcard,
            resultTypeInterface?.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
    }

    public static string FormatTransports(RequestTransports transports)
    {
        var flags = new List<string>(4);

        if (transports.HasFlag(RequestTransports.Callable))
            flags.Add("Callable");

        if (transports.HasFlag(RequestTransports.Queuable))
            flags.Add("Queuable");

        if (transports.HasFlag(RequestTransports.Notifiable))
            flags.Add("Notifiable");

        if (transports.HasFlag(RequestTransports.Schedulable))
            flags.Add("Schedulable");

        return string.Join(" | ", flags.Select(flag => $"global::Cntryl.Portia.RequestTransports.{flag}"));
    }

    public static string FormatStringLiteral(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    static bool ImplementsInterface(INamedTypeSymbol symbol, string metadataName) =>
        symbol.AllInterfaces.Any(iface => iface.ToDisplayString() == metadataName);
}

// Mirrors Cntryl.Portia.RequestTransports in Portia.Abstractions for this generator's own
// bookkeeping — Portia.Generators does not (and should not) reference Portia.Abstractions, so it
// cannot use the real enum directly. Only the flag names matter; they are emitted as text (see
// RequestTransportDiscovery.FormatTransports) against the real type in the consumer's compilation.
[Flags]
enum RequestTransports
{
    None = 0,
    Callable = 1,
    Queuable = 2,
    Notifiable = 4,
    Schedulable = 8,
}

sealed class RequestTransportComponent(
    string typeName,
    RequestTransports transports,
    string realm,
    string area,
    string resource,
    string operation,
    string? resultType)
{
    public string TypeName { get; } = typeName;

    public RequestTransports Transports { get; } = transports;

    public string Realm { get; } = realm;

    public string Area { get; } = area;

    public string Resource { get; } = resource;

    public string Operation { get; } = operation;

    public string? ResultType { get; } = resultType;
}
