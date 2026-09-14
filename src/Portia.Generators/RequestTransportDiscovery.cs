using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cntryl.Portia;

/// <summary>
///     Discovers every routed, transport-marked request in a compilation — shared by every generator
///     that needs it (<see cref="PortiaServiceRegistrationGenerator" /> for the DI transport registry,
///     generated typed RPC descriptors for RPC worker registration) so the discovery
///     rules live in exactly one place, not duplicated per generator.
/// </summary>
static class RequestTransportDiscovery
{
    const string RequestRouteAttributeMetadataName = "Cntryl.Portia.RequestRouteAttribute";
    const string DiscriminatorAttributeMetadataName = "Cntryl.Portia.DiscriminatorAttribute";
    const string RequestTransportAttributeMetadataName = "Cntryl.Portia.RequestTransportAttribute";
    const string RequestWithResultMetadataName = "IRequest`1";
    const string StreamRequestMetadataName = "IStreamRequest`1";

    public static bool IsCandidate(SyntaxNode node) => node is TypeDeclarationSyntax;

    public static RequestTransportComponent? GetRequestTransportComponent(GeneratorSyntaxContext context)
    {
        var declaration = (TypeDeclarationSyntax)context.Node;
        return context.SemanticModel.GetDeclaredSymbol(declaration) is INamedTypeSymbol symbol
               && GetRequestTransportComponent(symbol) is { } component
            ? component with { Location = DiagnosticLocation.From(declaration.Identifier.GetLocation()) }
            : null;
    }

    public static InvalidDiscriminator? GetInvalidRequestDiscriminator(GeneratorSyntaxContext context)
    {
        var declaration = (TypeDeclarationSyntax)context.Node;
        if (context.SemanticModel.GetDeclaredSymbol(declaration) is not INamedTypeSymbol symbol
            || symbol.IsAbstract
            || IsStream(symbol)
            || !HasTransportMarker(symbol)
            || !symbol.GetAttributes().Any(attribute =>
                attribute.AttributeClass?.ToDisplayString() == RequestRouteAttributeMetadataName))
        {
            return null;
        }

        var attribute = symbol.GetAttributes()
            .FirstOrDefault(candidate =>
                candidate.AttributeClass?.ToDisplayString() == DiscriminatorAttributeMetadataName);
        var name = attribute?.ConstructorArguments.ElementAtOrDefault(0).Value as string;
        var version = attribute?.ConstructorArguments.ElementAtOrDefault(1).Value as int? ?? 0;
        return attribute is not null && !string.IsNullOrWhiteSpace(name) && version > 0
            ? null
            : new InvalidDiscriminator(symbol.ToDisplayString(),
                DiagnosticLocation.From(declaration.Identifier.GetLocation()));
    }

    public static InvalidRoute? GetInvalidRoute(GeneratorSyntaxContext context)
    {
        var declaration = (TypeDeclarationSyntax)context.Node;
        if (context.SemanticModel.GetDeclaredSymbol(declaration) is not INamedTypeSymbol symbol
            || symbol.IsAbstract
            || IsStream(symbol)
            || !HasTransportMarker(symbol))
        {
            return null;
        }

        var attribute = symbol.GetAttributes()
            .FirstOrDefault(candidate =>
                candidate.AttributeClass?.ToDisplayString() == RequestRouteAttributeMetadataName);
        if (attribute is null || attribute.ConstructorArguments.Length != 4)
        {
            return null;
        }

        foreach (var argument in attribute.ConstructorArguments)
        {
            var segment = argument.Value as string;
            if (!IsValidRouteSegment(segment))
            {
                return new InvalidRoute(symbol.ToDisplayString(), segment ?? string.Empty,
                    DiagnosticLocation.From(declaration.Identifier.GetLocation()));
            }
        }

        return null;
    }

    public static RequestTransportComponent? GetRequestTransportComponent(ITypeSymbol? type)
    {
        if (type is not INamedTypeSymbol symbol || symbol.IsAbstract)
        {
            return null;
        }

        // A streamed request has no RPC, queue, notice, or schedule transport at all today —
        // only MapPortiaGetStream/MapPortiaGetSse exist for it, wired directly by the HTTP
        // binding generator without going through this transport table. ICallable on a stream
        // request means "callable over HTTP", not "callable over Fitz RPC" the way it does for
        // IRequest/IRequest<T> — so it's excluded here rather than mis-registered as RPC-eligible.
        if (IsStream(symbol))
        {
            return null;
        }

        var routeAttribute = symbol.GetAttributes()
            .FirstOrDefault(attribute =>
                attribute.AttributeClass?.ToDisplayString() == RequestRouteAttributeMetadataName);
        var discriminatorAttribute = symbol.GetAttributes()
            .FirstOrDefault(attribute =>
                attribute.AttributeClass?.ToDisplayString() == DiscriminatorAttributeMetadataName);

        if (routeAttribute is null || routeAttribute.ConstructorArguments.Length != 4
                                   || discriminatorAttribute is null ||
                                   discriminatorAttribute.ConstructorArguments.Length != 2)
        {
            return null;
        }

        var transports = GetTransportIds(symbol);
        if (transports.Length == 0)
        {
            return null;
        }

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
            (int?)discriminatorAttribute.ConstructorArguments[1].Value ?? 0,
            (string?)discriminatorAttribute.ConstructorArguments[0].Value ?? string.Empty,
            resultTypeInterface?.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
    }

    public static string FormatTransports(IReadOnlyList<string> transports) =>
        "new global::Cntryl.Portia.RequestTransportId[] { " + string.Join(", ", transports.Select(id =>
            $"new global::Cntryl.Portia.RequestTransportId({FormatStringLiteral(id)})")) + " }";

    public static string FormatStringLiteral(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    static bool ImplementsInterface(INamedTypeSymbol symbol, string metadataName) =>
        symbol.AllInterfaces.Any(iface => iface.ToDisplayString() == metadataName);

    static bool IsStream(INamedTypeSymbol symbol) => symbol.AllInterfaces.Any(iface =>
        iface.OriginalDefinition.ContainingNamespace.ToDisplayString() == "Cntryl.Portia"
        && iface.OriginalDefinition.MetadataName == StreamRequestMetadataName);

    static bool HasTransportMarker(INamedTypeSymbol symbol) => GetTransportIds(symbol).Length != 0;

    static string[] GetTransportIds(INamedTypeSymbol symbol) => symbol.AllInterfaces
        .SelectMany(iface => iface.GetAttributes())
        .Where(attribute => attribute.AttributeClass?.ToDisplayString() == RequestTransportAttributeMetadataName)
        .Select(attribute => attribute.ConstructorArguments.ElementAtOrDefault(0).Value as string)
        .Where(static id => !string.IsNullOrWhiteSpace(id))
        .Select(static id => id!)
        .Distinct(StringComparer.Ordinal)
        .OrderBy(static id => id, StringComparer.Ordinal)
        .ToArray();

    static bool IsValidRouteSegment(string? segment) => !string.IsNullOrWhiteSpace(segment)
                                                        && (segment == "*" || segment.All(character =>
                                                            char.IsLetterOrDigit(character) ||
                                                            character is '.' or '_' or '-' or '~'));
}
