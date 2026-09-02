using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Cntryl.Portia;

/// <summary>
/// Generates event dispatchers for partial projector and reactor classes, discovering
/// IProjectorHandler/IReactorHandler implementations, so a missing or mistyped handler is a
/// compile error, not a silently unhandled event. Aggregates dispatch differently — an explicit
/// <c>On&lt;TEvent&gt;(handler)</c> delegate registered in the constructor (see
/// <c>Aggregate.On</c> in Portia.Abstractions) rather than a generated interface-driven switch — since forcing an
/// aggregate's event-application methods to be public just to satisfy an interface isn't a
/// tradeoff worth making there; <c>On&lt;TEvent&gt;</c> gets the same compile-time signature
/// safety from a plain generic delegate conversion instead.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class ProjectorReactorEventDispatcherGenerator : IIncrementalGenerator
{
    const string ProjectorMetadataName = "Cntryl.Portia.Projector`1";
    const string ReactorMetadataName = "Cntryl.Portia.Reactor";
    const string ProjectorEventHandlerMetadataName = "Cntryl.Portia.IProjectorHandler`2";
    const string ReactorEventHandlerMetadataName = "Cntryl.Portia.IReactorHandler`1";

    static readonly DiagnosticDescriptor ProjectorMustBePartial = new(
        "PORTIA002",
        "Projector must be partial",
        "Projector '{0}' must be partial so Portia can generate event dispatch",
        "Portia",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    static readonly DiagnosticDescriptor ReactorMustBePartial = new(
        "PORTIA005",
        "Reactor must be partial",
        "Reactor '{0}' must be partial so Portia can generate event dispatch",
        "Portia",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var projectors = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is ClassDeclarationSyntax,
                static (syntaxContext, _) => GetProjector(syntaxContext))
            .Where(static projector => projector is not null);

        context.RegisterSourceOutput(projectors, static (sourceContext, projector) =>
        {
            if (projector is not null)
                Generate(sourceContext, projector);
        });

        var reactors = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is ClassDeclarationSyntax,
                static (syntaxContext, _) => GetReactor(syntaxContext))
            .Where(static reactor => reactor is not null);

        context.RegisterSourceOutput(reactors, static (sourceContext, reactor) =>
        {
            if (reactor is not null)
                Generate(sourceContext, reactor);
        });
    }

    static ProjectorModel? GetProjector(GeneratorSyntaxContext context)
    {
        var declaration = (ClassDeclarationSyntax)context.Node;
        var symbol = context.SemanticModel.GetDeclaredSymbol(declaration);
        var projectorBase = symbol is null ? null : GetProjectorBase(symbol);

        if (symbol is null || symbol.IsAbstract || projectorBase is null)
            return null;

        var handlerInterface = context.SemanticModel.Compilation.GetTypeByMetadataName(ProjectorEventHandlerMetadataName);

        if (handlerInterface is null)
            return null;

        var projectionType = projectorBase.TypeArguments[0];
        var handlers = symbol.AllInterfaces
            .Where(iface => SymbolEqualityComparer.Default.Equals(iface.OriginalDefinition, handlerInterface)
                && SymbolEqualityComparer.Default.Equals(iface.TypeArguments[1], projectionType))
            .Select(iface => iface.TypeArguments[0])
            .OrderByDescending(type => GetInheritanceDepth(type))
            .ThenBy(type => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), StringComparer.Ordinal)
            .Select(type => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
            .ToArray();

        return new ProjectorModel(
            symbol.Name,
            symbol.ContainingNamespace.ToDisplayString(),
            declaration.Modifiers.Any(SyntaxKind.PartialKeyword),
            declaration.Identifier.GetLocation(),
            projectionType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            handlers);
    }

    static void Generate(SourceProductionContext context, ProjectorModel projector)
    {
        if (!projector.IsPartial)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                ProjectorMustBePartial,
                projector.Location,
                projector.Name));
            return;
        }

        var source = new StringBuilder()
            .AppendLine("// <auto-generated />")
            .AppendLine("#nullable enable")
            .Append("namespace ")
            .Append(projector.Namespace)
            .AppendLine(";")
            .AppendLine()
            .Append("partial class ")
            .AppendLine(projector.Name)
            .AppendLine("{")
            .AppendLine("    protected override global::System.Threading.Tasks.ValueTask ProjectEventAsync(")
            .AppendLine("        global::Cntryl.Portia.DomainEventRecord record,")
            .Append("        global::Cntryl.Portia.IProjectorContext<")
            .Append(projector.ProjectionType)
            .AppendLine("> context,")
            .AppendLine("        global::System.Threading.CancellationToken ct)")
            .AppendLine("    {")
            .AppendLine("        switch (record.Ev)")
            .AppendLine("        {");

        foreach (var eventType in projector.EventTypes)
        {
            _ = source
                .Append("            case ")
                .Append(eventType)
                .AppendLine(" typed:")
                .Append("                return ((global::Cntryl.Portia.IProjectorHandler<")
                .Append(eventType)
                .Append(", ")
                .Append(projector.ProjectionType)
                .AppendLine(">)this).HandleAsync(typed, context, ct);");
        }

        _ = source
            // A projector's pattern is expected to span more than it handles — filtering is by
            // event type (its handler interfaces), not by narrowing the route pattern — so an
            // unhandled event type is silently skipped, not an error.
            .AppendLine("            default:")
            .AppendLine("                return global::System.Threading.Tasks.ValueTask.CompletedTask;")
            .AppendLine("        }")
            .AppendLine("    }")
            .AppendLine("}");

        context.AddSource($"{projector.Name}.ProjectorDispatcher.g.cs", SourceText.From(source.ToString(), Encoding.UTF8));
    }

    static ReactorModel? GetReactor(GeneratorSyntaxContext context)
    {
        var declaration = (ClassDeclarationSyntax)context.Node;
        var symbol = context.SemanticModel.GetDeclaredSymbol(declaration);

        if (symbol is null || symbol.IsAbstract || !InheritsFrom(symbol, ReactorMetadataName))
            return null;

        var handlerInterface = context.SemanticModel.Compilation.GetTypeByMetadataName(ReactorEventHandlerMetadataName);

        if (handlerInterface is null)
            return null;

        var handlers = symbol.AllInterfaces
            .Where(iface => SymbolEqualityComparer.Default.Equals(iface.OriginalDefinition, handlerInterface))
            .Select(iface => iface.TypeArguments[0])
            .OrderByDescending(type => GetInheritanceDepth(type))
            .ThenBy(type => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), StringComparer.Ordinal)
            .Select(type => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
            .ToArray();

        return new ReactorModel(
            symbol.Name,
            symbol.ContainingNamespace.ToDisplayString(),
            declaration.Modifiers.Any(SyntaxKind.PartialKeyword),
            declaration.Identifier.GetLocation(),
            handlers);
    }

    static void Generate(SourceProductionContext context, ReactorModel reactor)
    {
        if (!reactor.IsPartial)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                ReactorMustBePartial,
                reactor.Location,
                reactor.Name));
            return;
        }

        var source = new StringBuilder()
            .AppendLine("// <auto-generated />")
            .AppendLine("#nullable enable")
            .Append("namespace ")
            .Append(reactor.Namespace)
            .AppendLine(";")
            .AppendLine()
            .Append("partial class ")
            .AppendLine(reactor.Name)
            .AppendLine("{")
            .AppendLine("    protected override global::System.Threading.Tasks.ValueTask ReactToEventAsync(")
            .AppendLine("        global::Cntryl.Portia.DomainEventRecord record,")
            .AppendLine("        global::System.Threading.CancellationToken ct)")
            .AppendLine("    {")
            .AppendLine("        switch (record.Ev)")
            .AppendLine("        {");

        foreach (var eventType in reactor.EventTypes)
        {
            _ = source
                .Append("            case ")
                .Append(eventType)
                .AppendLine(" typed:")
                .Append("                return ((global::Cntryl.Portia.IReactorHandler<")
                .Append(eventType)
                .AppendLine(">)this).HandleAsync(new global::Cntryl.Portia.ReactorContext<")
                .Append(eventType)
                .AppendLine(">(typed), ct);");
        }

        _ = source
            // A reactor's pattern is expected to span more than it handles — filtering is by
            // event type (its handler interfaces), not by narrowing the route pattern — so an
            // unhandled event type is silently skipped, not an error.
            .AppendLine("            default:")
            .AppendLine("                return global::System.Threading.Tasks.ValueTask.CompletedTask;")
            .AppendLine("        }")
            .AppendLine("    }")
            .AppendLine("}");

        context.AddSource($"{reactor.Name}.ReactorDispatcher.g.cs", SourceText.From(source.ToString(), Encoding.UTF8));
    }

    static INamedTypeSymbol? GetProjectorBase(INamedTypeSymbol symbol)
    {
        for (var current = symbol.BaseType; current is not null; current = current.BaseType)
        {
            if (current.OriginalDefinition.MetadataName == ProjectorMetadataName.Split('.').Last()
                && current.OriginalDefinition.ContainingNamespace.ToDisplayString() == "Cntryl.Portia")
            {
                return current;
            }
        }

        return null;
    }

    static bool InheritsFrom(INamedTypeSymbol symbol, string metadataName)
    {
        for (var current = symbol.BaseType; current is not null; current = current.BaseType)
        {
            if (current.ToDisplayString() == metadataName)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Counts the number of base types between <paramref name="symbol"/> and <see cref="object"/>,
    /// used to order generated switch cases from most to least derived so a subtype's case is never
    /// shadowed by an ancestor's case.
    /// </summary>
    static int GetInheritanceDepth(ITypeSymbol? symbol)
    {
        var depth = 0;

        for (var current = symbol?.BaseType; current is not null; current = current.BaseType)
            depth++;

        return depth;
    }

    sealed class ProjectorModel(
        string name,
        string @namespace,
        bool isPartial,
        Location location,
        string projectionType,
        string[] eventTypes)
    {
        public string Name { get; } = name;

        public string Namespace { get; } = @namespace;

        public bool IsPartial { get; } = isPartial;

        public Location Location { get; } = location;

        public string ProjectionType { get; } = projectionType;

        public string[] EventTypes { get; } = eventTypes;
    }

    sealed class ReactorModel(
        string name,
        string @namespace,
        bool isPartial,
        Location location,
        string[] eventTypes)
    {
        public string Name { get; } = name;

        public string Namespace { get; } = @namespace;

        public bool IsPartial { get; } = isPartial;

        public Location Location { get; } = location;

        public string[] EventTypes { get; } = eventTypes;
    }
}
