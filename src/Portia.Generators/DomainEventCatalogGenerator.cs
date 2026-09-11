using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cntryl.Portia;

/// <summary>
///     Discovers every concrete <c>DomainEvent</c> type in the compilation and emits
///     <c>DomainEventTypeCatalog.AddPortiaGeneratedDomainEvents()</c>, registering each one under its
///     logical name and schema version (see <c>DiscriminatorAttribute</c>) — no per-type
///     <c>.Register&lt;T&gt;()</c> call to remember, matching the zero-boilerplate discovery already
///     used for reactors, projectors, and request transports.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class DomainEventCatalogGenerator : IIncrementalGenerator
{
    const string DomainEventMetadataName = "Cntryl.Portia.DomainEvent";
    const string DiscriminatorAttributeMetadataName = "Cntryl.Portia.DiscriminatorAttribute";
    const string JsonDomainEventUpcasterMetadataName = "Cntryl.Portia.IJsonDomainEventUpcaster";

    static readonly DiagnosticDescriptor UnknownUpcasterEventName = new(
        "PORTIA012",
        "Upcaster references an unknown event name",
        "'{0}'.EventName returns '{1}', which does not match the logical name of any DomainEvent type in the compilation",
        "Portia",
        DiagnosticSeverity.Warning,
        true);

    static readonly DiagnosticDescriptor DuplicateDiscriminator = new("PORTIA023",
        "Duplicate domain-event discriminator",
        "Domain-event CLR types '{0}' and '{1}' both declare discriminator '{2}' version {3}", "Portia",
        DiagnosticSeverity.Error, true);

    static readonly DiagnosticDescriptor InvalidDiscriminator = new("PORTIA021",
        "Missing or invalid domain-event discriminator",
        "Domain event '{0}' must declare [Discriminator(\"name\", version)] with a non-empty name and positive version",
        "Portia", DiagnosticSeverity.Error, true);

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var events = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is ClassDeclarationSyntax or RecordDeclarationSyntax,
                static (syntaxContext, _) => GetEventModel(syntaxContext))
            .Where(static model => model is not null)
            .Select(static (model, _) => model!);
        var eventModels = events.Collect();
        var eventRegistrations = events
            .Select(static (model, _) => new EventRegistration(model.TypeName, model.Name, model.Version))
            .Collect()
            .WithTrackingName("DomainEventCatalogRegistrations");
        var invalidDiscriminators = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is ClassDeclarationSyntax or RecordDeclarationSyntax,
                static (syntaxContext, _) => GetInvalidEventDiscriminator(syntaxContext))
            .Where(static model => model is not null)
            .Select(static (model, _) => model!)
            .Collect();
        context.RegisterSourceOutput(invalidDiscriminators, static (sourceContext, invalid) =>
        {
            foreach (var model in invalid)
                sourceContext.ReportDiagnostic(Diagnostic.Create(InvalidDiscriminator, model.Location.ToLocation(), model.TypeName));
        });

        var upcasters = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is ClassDeclarationSyntax,
                static (syntaxContext, _) => GetUpcasterEventName(syntaxContext))
            .Where(static upcaster => upcaster is not null)
            .Select(static (upcaster, _) => upcaster!)
            .Collect();

        context.RegisterSourceOutput(
            eventModels.Combine(upcasters),
            static (sourceContext, pair) => GenerateDiagnostics(sourceContext, pair.Left, pair.Right));
        context.RegisterSourceOutput(eventRegistrations, static (sourceContext, registrations) =>
            GenerateCatalog(sourceContext, registrations));
    }

    static void GenerateDiagnostics(
        SourceProductionContext context,
        ImmutableArray<EventModel> events,
        ImmutableArray<UpcasterModel> upcasters)
    {
        foreach (var upcaster in upcasters)
        {
            if (!events.Any(e => e.Name == upcaster.EventName))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    UnknownUpcasterEventName,
                    upcaster.Location.ToLocation(),
                    upcaster.TypeName,
                    upcaster.EventName));
            }
        }

        foreach (var group in events.GroupBy(ev => (ev.Name, ev.Version))
                     .Where(group => group.Select(ev => ev.TypeName).Distinct().Count() > 1))
        {
            var declarations = group.GroupBy(ev => ev.TypeName).Select(types => types.First()).ToArray();
            var original = declarations[0];
            foreach (var duplicate in declarations.Skip(1))
                context.ReportDiagnostic(Diagnostic.Create(DuplicateDiscriminator, duplicate.Location.ToLocation(),
                    original.TypeName, duplicate.TypeName, group.Key.Name, group.Key.Version));
        }

    }

    static void GenerateCatalog(
        SourceProductionContext context,
        ImmutableArray<EventRegistration> events)
    {
        if (events.IsDefaultOrEmpty)
            return;

        var builder = new StringBuilder();
        _ = builder.AppendLine("// <auto-generated />");
        _ = builder.AppendLine("namespace Cntryl.Portia;");
        _ = builder.AppendLine();
        _ = builder.AppendLine("static class DomainEventTypeCatalogGeneratedExtensions");
        _ = builder.AppendLine("{");
        _ = builder.AppendLine(
            "    public static global::Cntryl.Portia.DomainEventTypeCatalog AddPortiaGeneratedDomainEvents(this global::Cntryl.Portia.DomainEventTypeCatalog catalog)");
        _ = builder.AppendLine("    {");
        _ = builder.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(catalog);");

        foreach (var ev in events.Distinct())
        {
            _ = builder.Append("        _ = catalog.Register<global::").Append(ev.TypeName).Append(">(")
                .Append(ev.Version).Append(", ").Append(RequestTransportDiscovery.FormatStringLiteral(ev.Name))
                .AppendLine(");");
        }

        _ = builder.AppendLine("        return catalog;");
        _ = builder.AppendLine("    }");
        _ = builder.AppendLine("}");

        context.AddSource("DomainEventTypeCatalogGeneratedExtensions.g.cs", builder.ToString());
    }

    static EventModel? GetEventModel(GeneratorSyntaxContext context)
    {
        var declaration = (TypeDeclarationSyntax)context.Node;

        if (context.SemanticModel.GetDeclaredSymbol(declaration) is not INamedTypeSymbol symbol
            || symbol.IsAbstract
            || !IsCatalogable(symbol)
            || !InheritsFrom(symbol, DomainEventMetadataName))
        {
            return null;
        }

        var (name, version) = GetSchemaIdentity(symbol);
        return name is null
            ? null
            : new EventModel(symbol.ToDisplayString(), name, version,
                DiagnosticLocation.From(declaration.Identifier.GetLocation()));
    }

    static InvalidEventDiscriminator? GetInvalidEventDiscriminator(GeneratorSyntaxContext context)
    {
        var declaration = (TypeDeclarationSyntax)context.Node;
        if (context.SemanticModel.GetDeclaredSymbol(declaration) is not INamedTypeSymbol symbol
            || symbol.IsAbstract
            || !IsCatalogable(symbol)
            || !InheritsFrom(symbol, DomainEventMetadataName))
        {
            return null;
        }

        var (name, version) = GetSchemaIdentity(symbol);
        return !string.IsNullOrWhiteSpace(name) && version > 0
            ? null
            : new InvalidEventDiscriminator(symbol.ToDisplayString(),
                DiagnosticLocation.From(declaration.Identifier.GetLocation()));
    }

    static UpcasterModel? GetUpcasterEventName(GeneratorSyntaxContext context)
    {
        var declaration = (ClassDeclarationSyntax)context.Node;

        if (context.SemanticModel.GetDeclaredSymbol(declaration) is not INamedTypeSymbol symbol
            || symbol.IsAbstract
            || !ImplementsInterface(symbol, JsonDomainEventUpcasterMetadataName))
        {
            return null;
        }

        var eventNameProperty = symbol.GetMembers("EventName").OfType<IPropertySymbol>().FirstOrDefault();

        return eventNameProperty?.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is not
                   PropertyDeclarationSyntax propertySyntax
               || propertySyntax.ExpressionBody?.Expression is not LiteralExpressionSyntax literal
               || context.SemanticModel.GetConstantValue(literal) is not { HasValue: true, Value: string eventName }
            ? null
            : new UpcasterModel(symbol.ToDisplayString(), eventName,
                DiagnosticLocation.From(declaration.Identifier.GetLocation()));
    }

    static (string? Name, int Version) GetSchemaIdentity(INamedTypeSymbol symbol)
    {
        var attribute = symbol.GetAttributes()
            .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == DiscriminatorAttributeMetadataName);

        if (attribute is null || attribute.ConstructorArguments.Length != 2)
            return (null, 0);

        var name = attribute.ConstructorArguments[0].Value as string;
        var version = attribute.ConstructorArguments[1].Value as int? ?? 0;
        return (name, version);
    }

    static bool InheritsFrom(INamedTypeSymbol symbol, string baseTypeMetadataName)
    {
        for (var type = symbol.BaseType; type is not null; type = type.BaseType)
        {
            if (type.ToDisplayString() == baseTypeMetadataName)
                return true;
        }

        return false;
    }

    static bool ImplementsInterface(INamedTypeSymbol symbol, string interfaceMetadataName) =>
        symbol.AllInterfaces.Any(i => i.ToDisplayString() == interfaceMetadataName);

    // A type the generated catalog can name as a closed generic argument. An open generic event
    // cannot be one — Register<T> needs a closed type, and one [Discriminator] cannot identify a
    // schema shared by every closed form — so emitting it would produce source that does not
    // compile rather than a registration.
    static bool IsCatalogable(INamedTypeSymbol symbol) =>
        !symbol.IsGenericType && IsAccessibleFromGeneratedCode(symbol);

    // Only a type (and every enclosing type, for a nested declaration) that's at least internal
    // can be named as a generic type argument from the generated top-level extension method —
    // a private or protected nested event type is invisible to it, same as it would be to any
    // other hand-written code outside the declaring type.
    static bool IsAccessibleFromGeneratedCode(INamedTypeSymbol symbol)
    {
        for (var type = symbol; type is not null; type = type.ContainingType)
        {
            if (type.DeclaredAccessibility is Accessibility.Private or Accessibility.Protected
                or Accessibility.ProtectedAndInternal)
            {
                return false;
            }
        }

        return true;
    }

    sealed record EventModel(string TypeName, string Name, int Version, DiagnosticLocation Location);

    sealed record EventRegistration(string TypeName, string Name, int Version);

    sealed record UpcasterModel(string TypeName, string EventName, DiagnosticLocation Location);

    sealed record InvalidEventDiscriminator(string TypeName, DiagnosticLocation Location);
}
