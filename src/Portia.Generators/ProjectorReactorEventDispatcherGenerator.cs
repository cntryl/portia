using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Cntryl.Portia;

/// <summary>Generates ordered, typed event and batch dispatch for explicitly registered processors.</summary>
[Generator(LanguageNames.CSharp)]
public sealed class ProjectorReactorEventDispatcherGenerator : IIncrementalGenerator
{
    static readonly DiagnosticDescriptor ProjectorMustBePartial = new("PORTIA002", "Projector must be partial",
        "Projector '{0}' must be partial so Portia can generate event dispatch", "Portia", DiagnosticSeverity.Error,
        true);

    static readonly DiagnosticDescriptor ReactorMustBePartial = new("PORTIA005", "Reactor must be partial",
        "Reactor '{0}' must be partial so Portia can generate event dispatch", "Portia", DiagnosticSeverity.Error,
        true);

    static readonly DiagnosticDescriptor InvalidBatchHandler = new("PORTIA017", "Invalid batch handler",
        "Processor '{0}' must use a batch base to implement handler '{1}'",
        "Portia", DiagnosticSeverity.Error, true);

    static readonly DiagnosticDescriptor AmbiguousHandlerMode = new("PORTIA028", "Ambiguous event handler mode",
        "Processor '{0}' selects both single and batch handling for event '{1}'",
        "Portia", DiagnosticSeverity.Error, true);

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var processors = context.SyntaxProvider.CreateSyntaxProvider(
            static (node, _) => node is ClassDeclarationSyntax,
            static (ctx, _) => GetProcessor(ctx)).Where(static item => item is not null);
        context.RegisterSourceOutput(processors, static (ctx, item) => Generate(ctx, item!));
    }

    static Processor? GetProcessor(GeneratorSyntaxContext context)
    {
        var declaration = (ClassDeclarationSyntax)context.Node;
        if (context.SemanticModel.GetDeclaredSymbol(declaration) is not INamedTypeSymbol symbol || symbol.IsAbstract ||
            !IsFirstDeclaration(symbol, declaration))
        {
            return null;
        }

        var projector = InheritsFrom(symbol, "Cntryl.Portia.Projector");
        if (!projector && !InheritsFrom(symbol, "Cntryl.Portia.Reactor"))
            return null;

        var singleName = projector ? "IProjectorHandler" : "IReactorHandler";
        var batchName = projector ? "IBatchProjectorHandler" : "IBatchReactorHandler";
        var handlers = symbol.AllInterfaces.Where(i => i.ContainingNamespace.ToDisplayString() == "Cntryl.Portia"
                                                       && i.TypeArguments.Length == 1 &&
                                                       (i.Name == singleName || i.Name == batchName))
            .OrderByDescending(i => GetInheritanceDepth(i.TypeArguments[0]))
            .ThenBy(i => i.TypeArguments[0].ToDisplayString(), StringComparer.Ordinal)
            .Select(i => new Handler(i.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                i.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
                i.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat), i.Name == batchName)).ToArray();
        return handlers.Length == 0
            ? null
            : new Processor(symbol.Name, symbol.ToDisplayString(),
                symbol.ContainingNamespace.IsGlobalNamespace ? null : symbol.ContainingNamespace.ToDisplayString(),
                GetParents(symbol), GeneratedTypeShape.HintName(symbol),
                DiagnosticLocation.From(symbol.Locations.FirstOrDefault()),
                GeneratedTypeShape.IsSupported(symbol, true),
                declaration.Modifiers.Any(SyntaxKind.PartialKeyword), projector,
                InheritsFrom(symbol, projector ? "Cntryl.Portia.BatchProjector" : "Cntryl.Portia.BatchReactor"),
                handlers);
    }

    static void Generate(SourceProductionContext context, Processor processor)
    {
        if (!processor.Partial)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                processor.Projector ? ProjectorMustBePartial : ReactorMustBePartial,
                processor.Location.ToLocation(), processor.Name));
            return;
        }

        if (!processor.ShapeSupported)
        {
            context.ReportDiagnostic(Diagnostic.Create(GeneratedTypeShape.Unsupported,
                processor.Location.ToLocation(), processor.DisplayName));
            return;
        }
        var invalidBatchHandler = processor.Handlers.FirstOrDefault(h => h.Batch && !processor.Batch);
        if (invalidBatchHandler is not null)
        {
            context.ReportDiagnostic(Diagnostic.Create(InvalidBatchHandler, processor.Location.ToLocation(),
                processor.Name, invalidBatchHandler.InterfaceType));
            return;
        }

        var ambiguousEvent = processor.Handlers.GroupBy(h => h.Type).FirstOrDefault(g => g.Count() > 1);
        if (ambiguousEvent is not null)
        {
            context.ReportDiagnostic(Diagnostic.Create(AmbiguousHandlerMode, processor.Location.ToLocation(),
                processor.Name, ambiguousEvent.First().DisplayType));
            return;
        }

        var source = OpenShape(processor);
        _ = source.AppendLine(processor.Projector
                ? "protected override global::System.Threading.Tasks.ValueTask ProjectEventAsync(global::Cntryl.Portia.DomainEventRecord record, global::Cntryl.Portia.IProjectorContext context, global::System.Threading.CancellationToken ct) {"
                : "protected override global::System.Threading.Tasks.ValueTask ReactToEventAsync(global::Cntryl.Portia.DomainEventRecord record, global::Cntryl.Portia.IExecutionContext execution, global::System.Threading.CancellationToken ct) {")
            .AppendLine("switch (record.Event) {");
        foreach (var handler in processor.Handlers.Where(h => !h.Batch))
        {
            _ = source.Append("case ").Append(handler.Type).Append(" typed: return ((global::Cntryl.Portia.")
                .Append(processor.Projector ? "IProjectorHandler<" : "IReactorHandler<").Append(handler.Type)
                .Append(">)this).HandleAsync(")
                .Append(processor.Projector
                    ? "typed, context, ct);"
                    : "new global::Cntryl.Portia.ReactorContext<" + handler.Type + ">(typed, record, execution), ct);")
                .AppendLine();
        }

        _ = source.AppendLine("default: return global::System.Threading.Tasks.ValueTask.CompletedTask; } }");
        if (processor.Handlers.Any(h => h.Batch))
            AppendBatch(source, processor);
        _ = source.AppendLine("}");
        for (var index = 0; index < processor.Parents.Length; index++)
            _ = source.AppendLine("}");
        context.AddSource(processor.HintName + ".EventDispatcher.g.cs",
            SourceText.From(source.ToString(), Encoding.UTF8));
    }

    static void AppendBatch(StringBuilder source, Processor processor)
    {
        _ = source.AppendLine(
            "private static int PortiaHandlerKind(global::Cntryl.Portia.DomainEvent ev) => ev switch {");
        for (var i = 0; i < processor.Handlers.Length; i++)
            _ = source.Append(processor.Handlers[i].Type).Append(" => ").Append(i).AppendLine(",");
        _ = source.AppendLine("_ => -1 };");
        _ = source.AppendLine(processor.Projector
            ? "protected override async global::System.Threading.Tasks.ValueTask ProjectBatchAsync(global::System.Collections.Generic.IReadOnlyList<global::Cntryl.Portia.DomainEventRecord> records, global::Cntryl.Portia.IProjectorContext context, global::System.Threading.CancellationToken ct) {"
            : "protected override async global::System.Threading.Tasks.ValueTask ReactBatchAsync(global::System.Collections.Generic.IReadOnlyList<global::Cntryl.Portia.IReactorContext> records, global::System.Threading.CancellationToken ct) {");
        var ev = processor.Projector ? "records[i].Event" : "records[i].Source.Event";
        _ = source.Append(
                "for (var i = 0; i < records.Count;) { ct.ThrowIfCancellationRequested(); switch (PortiaHandlerKind(")
            .Append(ev).AppendLine(")) {");
        for (var i = 0; i < processor.Handlers.Length; i++)
        {
            var handler = processor.Handlers[i];
            if (!handler.Batch)
            {
                continue;
            }
            else
            {
                var type = processor.Projector
                    ? handler.Type
                    : "global::Cntryl.Portia.IReactorContext<" + handler.Type + ">";
                _ = source.Append("case ").Append(i)
                    .Append(": { var batch = new global::System.Collections.Generic.List<")
                    .Append(type).AppendLine(">();")
                    .Append("do { batch.Add(").Append(processor.Projector
                        ? "(" + handler.Type + ")" + ev
                        : "new global::Cntryl.Portia.ReactorContext<" + handler.Type + ">((" + handler.Type + ")" + ev +
                          ", records[i].Source, records[i])")
                    .Append("); i++; } while (i < records.Count && PortiaHandlerKind(").Append(ev).Append(") == ")
                    .Append(i)
                    .AppendLine(");")
                    .Append("await ((global::Cntryl.Portia.")
                    .Append(processor.Projector ? "IBatchProjectorHandler<" : "IBatchReactorHandler<")
                    .Append(handler.Type).Append(">)this).HandleAsync(batch, ")
                    .Append(processor.Projector ? "context, " : "").AppendLine("ct).ConfigureAwait(false); break; }");
            }
        }

        _ = source.AppendLine(processor.Projector
                ? "default: await ProjectEventAsync(records[i++], context, ct).ConfigureAwait(false); break;"
                : "default: var item = records[i++]; await ReactToEventAsync(item.Source, item, ct).ConfigureAwait(false); break;")
            .AppendLine("} } }");
    }

    static bool IsFirstDeclaration(INamedTypeSymbol symbol, ClassDeclarationSyntax declaration) =>
        symbol.DeclaringSyntaxReferences[0].SyntaxTree == declaration.SyntaxTree
        && symbol.DeclaringSyntaxReferences[0].Span == declaration.Span;

    static string[] GetParents(INamedTypeSymbol symbol)
    {
        var parents = new Stack<string>();
        for (var parent = symbol.ContainingType; parent is not null; parent = parent.ContainingType)
        {
            var kind = parent.IsRecord ? parent.IsValueType ? "record struct" : "record"
                : parent.TypeKind == TypeKind.Interface ? "interface"
                : parent.IsValueType ? "struct" : "class";
            parents.Push($"partial {kind} @{parent.Name} {{");
        }
        return [.. parents];
    }

    static StringBuilder OpenShape(Processor processor)
    {
        var source = new StringBuilder().AppendLine("// <auto-generated />").AppendLine("#nullable enable");
        if (processor.Namespace is not null)
            _ = source.Append("namespace ").Append(processor.Namespace).AppendLine(";");
        foreach (var parent in processor.Parents)
            _ = source.AppendLine(parent);

        return source.Append("partial class @").Append(processor.Name).AppendLine(" {");
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
    ///     Counts the number of base types between <paramref name="symbol" /> and <see cref="object" />,
    ///     used to order generated switch cases from most to least derived so a subtype's case is never
    ///     shadowed by an ancestor's case.
    /// </summary>
    static int GetInheritanceDepth(ITypeSymbol? symbol)
    {
        var depth = 0;

        for (var current = symbol?.BaseType; current is not null; current = current.BaseType)
            depth++;

        return depth;
    }

    sealed record Handler(string Type, string DisplayType, string InterfaceType, bool Batch);

    sealed class Processor(
        string name,
        string displayName,
        string? @namespace,
        string[] parents,
        string hintName,
        DiagnosticLocation location,
        bool shapeSupported,
        bool partial,
        bool projector,
        bool batch,
        Handler[] handlers) : IEquatable<Processor>
    {
        public string Name { get; } = name;
        public string DisplayName { get; } = displayName;
        public string? Namespace { get; } = @namespace;
        public string[] Parents { get; } = parents;
        public string HintName { get; } = hintName;
        public DiagnosticLocation Location { get; } = location;
        public bool ShapeSupported { get; } = shapeSupported;
        public bool Partial { get; } = partial;
        public bool Projector { get; } = projector;
        public bool Batch { get; } = batch;
        public Handler[] Handlers { get; } = handlers;
        public bool Equals(Processor? other) => other is not null && Name == other.Name &&
            DisplayName == other.DisplayName && Namespace == other.Namespace && HintName == other.HintName &&
            Location.Equals(other.Location) && ShapeSupported == other.ShapeSupported && Partial == other.Partial &&
            Projector == other.Projector && Batch == other.Batch && Parents.SequenceEqual(other.Parents) &&
            Handlers.SequenceEqual(other.Handlers);
        public override bool Equals(object? obj) => Equals(obj as Processor);
        public override int GetHashCode() => (Name, DisplayName, Namespace, HintName).GetHashCode();
    }
}
