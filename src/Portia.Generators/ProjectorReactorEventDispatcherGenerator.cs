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
        "Projector '{0}' must be partial so Portia can generate event dispatch", "Portia", DiagnosticSeverity.Error, true);
    static readonly DiagnosticDescriptor ReactorMustBePartial = new("PORTIA005", "Reactor must be partial",
        "Reactor '{0}' must be partial so Portia can generate event dispatch", "Portia", DiagnosticSeverity.Error, true);
    static readonly DiagnosticDescriptor InvalidBatchHandler = new("PORTIA017", "Invalid batch handler",
        "Processor '{0}' must use a batch base for batch handlers and select only one handler mode per event type", "Portia", DiagnosticSeverity.Error, true);

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
        if (context.SemanticModel.GetDeclaredSymbol(declaration) is not INamedTypeSymbol symbol || symbol.IsAbstract || !IsFirstDeclaration(symbol, declaration))
            return null;
        var projector = InheritsFrom(symbol, "Cntryl.Portia.Projector");
        if (!projector && !InheritsFrom(symbol, "Cntryl.Portia.Reactor"))
            return null;
        var singleName = projector ? "IProjectorHandler" : "IReactorHandler";
        var batchName = projector ? "IBatchProjectorHandler" : "IBatchReactorHandler";
        var handlers = symbol.AllInterfaces.Where(i => i.ContainingNamespace.ToDisplayString() == "Cntryl.Portia"
                && i.TypeArguments.Length == 1 && (i.Name == singleName || i.Name == batchName))
            .OrderByDescending(i => GetInheritanceDepth(i.TypeArguments[0]))
            .ThenBy(i => i.TypeArguments[0].ToDisplayString(), StringComparer.Ordinal)
            .Select(i => new Handler(i.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), i.Name == batchName)).ToArray();
        return handlers.Length == 0 ? null : new Processor(symbol, declaration.Modifiers.Any(SyntaxKind.PartialKeyword), projector,
            InheritsFrom(symbol, projector ? "Cntryl.Portia.BatchProjector" : "Cntryl.Portia.BatchReactor"), handlers);
    }

    static void Generate(SourceProductionContext context, Processor processor)
    {
        var symbol = processor.Symbol;
        if (!processor.Partial)
        {
            context.ReportDiagnostic(Diagnostic.Create(processor.Projector ? ProjectorMustBePartial : ReactorMustBePartial, symbol.Locations.FirstOrDefault(), symbol.Name));
            return;
        }
        if (!ValidateShape(context, symbol))
            return;
        if ((!processor.Batch && processor.Handlers.Any(h => h.Batch)) || processor.Handlers.GroupBy(h => h.Type).Any(g => g.Count() > 1))
        {
            context.ReportDiagnostic(Diagnostic.Create(InvalidBatchHandler, symbol.Locations.FirstOrDefault(), symbol.Name));
            return;
        }
        var source = OpenShape(symbol);
        _ = source.AppendLine(processor.Projector
            ? "protected override global::System.Threading.Tasks.ValueTask ProjectEventAsync(global::Cntryl.Portia.DomainEventRecord record, global::Cntryl.Portia.IProjectorContext context, global::System.Threading.CancellationToken ct) {"
            : "protected override global::System.Threading.Tasks.ValueTask ReactToEventAsync(global::Cntryl.Portia.DomainEventRecord record, global::Cntryl.Portia.IExecutionContext execution, global::System.Threading.CancellationToken ct) {")
            .AppendLine("switch (record.Event) {");
        foreach (var handler in processor.Handlers.Where(h => !h.Batch))
        {
            _ = source.Append("case ").Append(handler.Type).Append(" typed: return ((global::Cntryl.Portia.")
                .Append(processor.Projector ? "IProjectorHandler<" : "IReactorHandler<").Append(handler.Type).Append(">)this).HandleAsync(")
                .Append(processor.Projector ? "typed, context, ct);" : "new global::Cntryl.Portia.ReactorContext<" + handler.Type + ">(typed, record, execution), ct);").AppendLine();
        }
        _ = source.AppendLine("default: return global::System.Threading.Tasks.ValueTask.CompletedTask; } }");
        if (processor.Handlers.Any(h => h.Batch))
            AppendBatch(source, processor);
        _ = source.AppendLine("}");
        for (var parent = symbol.ContainingType; parent is not null; parent = parent.ContainingType)
            _ = source.AppendLine("}");
        context.AddSource(GeneratedTypeShape.HintName(symbol) + ".EventDispatcher.g.cs", SourceText.From(source.ToString(), Encoding.UTF8));
    }

    static void AppendBatch(StringBuilder source, Processor processor)
    {
        _ = source.AppendLine("private static int PortiaHandlerKind(global::Cntryl.Portia.DomainEvent ev) => ev switch {");
        for (var i = 0; i < processor.Handlers.Length; i++)
            _ = source.Append(processor.Handlers[i].Type).Append(" => ").Append(i).AppendLine(",");
        _ = source.AppendLine("_ => -1 };");
        _ = source.AppendLine(processor.Projector
            ? "protected override async global::System.Threading.Tasks.ValueTask ProjectBatchAsync(global::System.Collections.Generic.IReadOnlyList<global::Cntryl.Portia.DomainEventRecord> records, global::Cntryl.Portia.IProjectorContext context, global::System.Threading.CancellationToken ct) {"
            : "protected override async global::System.Threading.Tasks.ValueTask ReactBatchAsync(global::System.Collections.Generic.IReadOnlyList<global::Cntryl.Portia.IReactorContext> records, global::System.Threading.CancellationToken ct) {");
        var ev = processor.Projector ? "records[i].Event" : "records[i].Source.Event";
        _ = source.Append("for (var i = 0; i < records.Count;) { ct.ThrowIfCancellationRequested(); switch (PortiaHandlerKind(").Append(ev).AppendLine(")) {");
        for (var i = 0; i < processor.Handlers.Length; i++)
        {
            var handler = processor.Handlers[i];
            if (!handler.Batch)
                continue;
            var type = processor.Projector ? handler.Type : "global::Cntryl.Portia.IReactorContext<" + handler.Type + ">";
            _ = source.Append("case ").Append(i).Append(": { var batch = new global::System.Collections.Generic.List<").Append(type).AppendLine(">();")
                .Append("do { batch.Add(").Append(processor.Projector ? "(" + handler.Type + ")" + ev
                    : "new global::Cntryl.Portia.ReactorContext<" + handler.Type + ">((" + handler.Type + ")" + ev + ", records[i].Source, records[i])")
                .Append("); i++; } while (i < records.Count && PortiaHandlerKind(").Append(ev).Append(") == ").Append(i).AppendLine(");")
                .Append("await ((global::Cntryl.Portia.").Append(processor.Projector ? "IBatchProjectorHandler<" : "IBatchReactorHandler<")
                .Append(handler.Type).Append(">)this).HandleAsync(batch, ").Append(processor.Projector ? "context, " : "").AppendLine("ct).ConfigureAwait(false); break; }");
        }
        _ = source.AppendLine(processor.Projector
            ? "default: await ProjectEventAsync(records[i++], context, ct).ConfigureAwait(false); break;"
            : "default: var item = records[i++]; await ReactToEventAsync(item.Source, item, ct).ConfigureAwait(false); break;")
            .AppendLine("} } }");
    }

    static bool IsFirstDeclaration(INamedTypeSymbol symbol, ClassDeclarationSyntax declaration) =>
        symbol.DeclaringSyntaxReferences[0].SyntaxTree == declaration.SyntaxTree
        && symbol.DeclaringSyntaxReferences[0].Span == declaration.Span;

    static bool ValidateShape(SourceProductionContext context, INamedTypeSymbol symbol)
    {
        if (GeneratedTypeShape.IsSupported(symbol, requirePartialContainers: true))
            return true;
        context.ReportDiagnostic(Diagnostic.Create(GeneratedTypeShape.Unsupported, symbol.Locations.FirstOrDefault(), symbol.ToDisplayString()));
        return false;
    }

    static StringBuilder OpenShape(INamedTypeSymbol symbol)
    {
        var source = new StringBuilder().AppendLine("// <auto-generated />").AppendLine("#nullable enable");
        if (!symbol.ContainingNamespace.IsGlobalNamespace)
            _ = source.Append("namespace ").Append(symbol.ContainingNamespace.ToDisplayString()).AppendLine(";");
        var parents = new Stack<INamedTypeSymbol>();
        for (var parent = symbol.ContainingType; parent is not null; parent = parent.ContainingType)
            parents.Push(parent);
        foreach (var parent in parents)
        {
            var kind = parent.IsRecord ? (parent.IsValueType ? "record struct" : "record")
                : parent.TypeKind == TypeKind.Interface ? "interface" : parent.IsValueType ? "struct" : "class";
            _ = source.Append("partial ").Append(kind).Append(" @").Append(parent.Name).AppendLine(" {");
        }
        return source.Append("partial class @").Append(symbol.Name).AppendLine(" {");
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

    sealed class Handler(string type, bool batch)
    {
        public string Type { get; } = type;
        public bool Batch { get; } = batch;
    }
    sealed class Processor(INamedTypeSymbol symbol, bool partial, bool projector, bool batch, Handler[] handlers)
    {
        public INamedTypeSymbol Symbol { get; } = symbol;
        public bool Partial { get; } = partial;
        public bool Projector { get; } = projector;
        public bool Batch { get; } = batch;
        public Handler[] Handlers { get; } = handlers;
    }
}
