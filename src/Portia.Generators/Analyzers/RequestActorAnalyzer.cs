using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Cntryl.Portia;

/// <summary>Checks request actor markers when an application opts into the convention.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RequestActorAnalyzer : DiagnosticAnalyzer
{
    const string OptInProperty = "build_property.PortiaRequireRequestActor";

    static readonly DiagnosticDescriptor Actor = new("PORTIA033", "Request needs exactly one actor marker",
        "Request contract '{0}' must implement exactly one actor marker; found {1}",
        "Portia", DiagnosticSeverity.Error, true);

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Actor];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            if (!start.Options.AnalyzerConfigOptionsProvider.GlobalOptions.TryGetValue(OptInProperty,
                    out var enabled) || !string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase))
                return;

            var requestBase = start.Compilation.GetTypeByMetadataName("Cntryl.Portia.IRequestBase");
            var actorTypes = new[]
            {
                start.Compilation.GetTypeByMetadataName("Cntryl.Portia.IPublicRequest"),
                start.Compilation.GetTypeByMetadataName("Cntryl.Portia.IAuthenticatedRequest"),
                start.Compilation.GetTypeByMetadataName("Cntryl.Portia.ISystemRequest")
            };

            if (requestBase is null || actorTypes.Any(actor => actor is null))
                return;

            start.RegisterSymbolAction(symbolContext => AnalyzeRequest(symbolContext, requestBase, actorTypes),
                SymbolKind.NamedType);
        });
    }

    static void AnalyzeRequest(
        SymbolAnalysisContext context,
        INamedTypeSymbol requestBase,
        INamedTypeSymbol?[] actorTypes
    )
    {
        var type = (INamedTypeSymbol)context.Symbol;

        if (type.TypeKind is not (TypeKind.Class or TypeKind.Struct) || type.IsAbstract || !IsRequest(type, requestBase))
        {
            return;
        }

        var actors = type.AllInterfaces
            .Where(iface => actorTypes.Any(actor => SymbolEqualityComparer.Default.Equals(iface, actor)))
            .Select(iface => iface.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        if (actors.Length == 1)
        {
            return;
        }

        var location = type.Locations.FirstOrDefault(location => location.IsInSource);
        context.ReportDiagnostic(Diagnostic.Create(Actor, location, type.Name,
            actors.Length == 0 ? "none" : string.Join(", ", actors)));
    }

    static bool IsRequest(INamedTypeSymbol type, INamedTypeSymbol requestBase) => type.AllInterfaces.Any(iface =>
        SymbolEqualityComparer.Default.Equals(iface, requestBase));
}
