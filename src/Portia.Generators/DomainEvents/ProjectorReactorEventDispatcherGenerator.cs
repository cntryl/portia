using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

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
            static (node, _) => node is ClassDeclarationSyntax && SyntaxFilters.HasBaseListOrPartial(node),
            static (ctx, ct) => GetProcessor(ctx, ct)).Where(static item => item is not null);
        context.RegisterSourceOutput(processors, static (ctx, item) => ReportDiagnostics(ctx, item!));
        var implementations = processors.Where(static processor => IsValid(processor!))
            .Select(static (processor, _) => processor!.WithoutLocation())
            .WithTrackingName("PortiaProcessorImplementations");
        context.RegisterSourceOutput(implementations, static (ctx, processor) => Generate(ctx, processor));
    }

    static Processor? GetProcessor(GeneratorSyntaxContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var declaration = (ClassDeclarationSyntax)context.Node;
        if (context.SemanticModel.GetDeclaredSymbol(declaration, ct) is not INamedTypeSymbol symbol || symbol.IsAbstract ||
            !IsFirstDeclaration(symbol, declaration))
        {
            return null;
        }

        var projector = InheritsFrom(symbol, "Cntryl.Portia.Projector");
        if (!projector && !InheritsFrom(symbol, "Cntryl.Portia.Reactor"))
            return null;

        var singleName = projector ? "IProjectorHandler" : "IReactorHandler";
        var batchName = projector ? "IBatchProjectorHandler" : "IBatchReactorHandler";
        bool IsHandlerInterface(INamedTypeSymbol i) => SymbolNames.IsInNamespace(i, "Cntryl.Portia")
                                                       && i.TypeArguments.Length == 1
                                                       && (i.Name == singleName || i.Name == batchName);
        // A concrete base processor already generates dispatch for every handler it implements, and this type
        // inherits that override; it needs its own only when it adds handlers.
        var dispatchingBase = symbol.BaseType;
        while (dispatchingBase is not null && dispatchingBase.IsAbstract)
            dispatchingBase = dispatchingBase.BaseType;
        if (dispatchingBase is not null
            && InheritsFrom(dispatchingBase, projector ? "Cntryl.Portia.Projector" : "Cntryl.Portia.Reactor")
            && symbol.AllInterfaces.Where(IsHandlerInterface)
                .All(handler => dispatchingBase.AllInterfaces.Contains(handler, SymbolEqualityComparer.Default)))
        {
            return null;
        }

        var handlers = symbol.AllInterfaces.Where(i => SymbolNames.IsInNamespace(i, "Cntryl.Portia")
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
                GeneratedTypeShape.UnsupportedReason(symbol, true),
                GeneratedTypeShape.UnsupportedReason(symbol) is null,
                declaration.Modifiers.Any(SyntaxKind.PartialKeyword), projector,
                InheritsFrom(symbol, projector ? "Cntryl.Portia.BatchProjector" : "Cntryl.Portia.BatchReactor"),
                handlers);
    }

    static void ReportDiagnostics(SourceProductionContext context, Processor processor)
    {
        if (!processor.Partial)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                processor.Projector ? ProjectorMustBePartial : ReactorMustBePartial,
                processor.Location.ToLocation(), processor.Name));
            return;
        }

        if (processor.UnsupportedReason is { } unsupportedReason)
        {
            // Every other shape check passed, so the reason is enclosing types that are not partial.
            var properties = processor.OnlyContainersUnsupported
                ? ImmutableDictionary<string, string?>.Empty.Add(GeneratedTypeShape.PartialContainersProperty, "true")
                : ImmutableDictionary<string, string?>.Empty;
            context.ReportDiagnostic(Diagnostic.Create(GeneratedTypeShape.Unsupported,
                processor.Location.ToLocation(), properties, processor.DisplayName, unsupportedReason));
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

    }

    static bool IsValid(Processor processor) => processor.Partial && processor.UnsupportedReason is null
        && !processor.Handlers.Any(handler => handler.Batch && !processor.Batch)
        && !processor.Handlers.GroupBy(handler => handler.Type).Any(group => group.Count() > 1);

    static void Generate(SourceProductionContext context, Processor processor)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        var source = OpenShape(processor);
        var depth = processor.Parents.Length + 1;
        Line(source, depth, processor.Projector
            ? "protected override global::System.Threading.Tasks.ValueTask<global::Cntryl.Portia.Result> ProjectEventAsync(global::Cntryl.Portia.DomainEventRecord record, global::Cntryl.Portia.IProjectorContext context, global::System.Threading.CancellationToken ct)"
            : "protected override global::System.Threading.Tasks.ValueTask<global::Cntryl.Portia.Result> ReactToEventAsync(global::Cntryl.Portia.DomainEventRecord record, global::Cntryl.Portia.IExecutionContext execution, global::System.Threading.CancellationToken ct)");
        Line(source, depth, "{");
        Line(source, depth + 1, "switch (record.Event)");
        Line(source, depth + 1, "{");
        foreach (var handler in processor.Handlers.Where(h => !h.Batch))
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            Line(source, depth + 2, "case " + handler.Type + " typed:");
            _ = source.Append(' ', (depth + 3) * 4)
                .Append("return global::Cntryl.Portia.ProcessingObservation.InvokeAsync(")
                .Append(SymbolDisplay.FormatLiteral(handler.InterfaceType, true))
                .Append(", ((global::Cntryl.Portia.")
                .Append(processor.Projector ? "IProjectorHandler<" : "IReactorHandler<").Append(handler.Type)
                .Append(">)this), ")
                .Append(processor.Projector
                    ? "new global::Cntryl.Portia.ProjectorContext<" + handler.Type + ">(typed, record, context), ct);"
                    : "new global::Cntryl.Portia.ReactorContext<" + handler.Type + ">(typed, record, execution), ct);")
                .Append('\n');
        }

        Line(source, depth + 2, "default:");
        Line(source, depth + 3, "return global::System.Threading.Tasks.ValueTask.FromResult(global::Cntryl.Portia.Result.Success);");
        Line(source, depth + 1, "}");
        Line(source, depth, "}");
        if (processor.Handlers.Any(h => h.Batch))
            AppendBatch(source, processor, depth, context.CancellationToken);
        for (var index = processor.Parents.Length; index >= 0; index--)
            Line(source, index, "}");
        GeneratedSource.Add(context, processor.HintName + ".EventDispatcher.g.cs", source.ToString());
    }

    static void AppendBatch(StringBuilder source, Processor processor, int depth, CancellationToken ct)
    {
        _ = source.Append('\n');
        Line(source, depth, "private static int PortiaHandlerKind(global::Cntryl.Portia.DomainEvent ev) => ev switch");
        Line(source, depth, "{");
        for (var i = 0; i < processor.Handlers.Length; i++)
            _ = source.Append(' ', (depth + 1) * 4).Append(processor.Handlers[i].Type).Append(" => ").Append(i).Append(",\n");
        Line(source, depth + 1, "_ => -1");
        Line(source, depth, "};");
        _ = source.Append('\n');
        Line(source, depth, processor.Projector
            ? "protected override async global::System.Threading.Tasks.ValueTask<global::Cntryl.Portia.Result> ProjectBatchAsync(global::System.Collections.Generic.IReadOnlyList<global::Cntryl.Portia.DomainEventRecord> records, global::Cntryl.Portia.IProjectorContext context, global::System.Threading.CancellationToken ct)"
            : "protected override async global::System.Threading.Tasks.ValueTask<global::Cntryl.Portia.Result> ReactBatchAsync(global::System.Collections.Generic.IReadOnlyList<global::Cntryl.Portia.IReactorContext> records, global::System.Threading.CancellationToken ct)");
        Line(source, depth, "{");
        Line(source, depth + 1, "for (var i = 0; i < records.Count;)");
        Line(source, depth + 1, "{");
        Line(source, depth + 2, "ct.ThrowIfCancellationRequested();");
        var ev = processor.Projector ? "records[i].Event" : "records[i].Source.Event";
        Line(source, depth + 2, "switch (PortiaHandlerKind(" + ev + "))");
        Line(source, depth + 2, "{");
        for (var i = 0; i < processor.Handlers.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var handler = processor.Handlers[i];
            if (handler.Batch)
            {
                var type = processor.Projector
                    ? "global::Cntryl.Portia.IProjectorContext<" + handler.Type + ">"
                    : "global::Cntryl.Portia.IReactorContext<" + handler.Type + ">";
                var itemEvent = processor.Projector ? "records[index].Event" : "records[index].Source.Event";
                _ = source.Append(' ', (depth + 3) * 4).Append("case ").Append(i).Append(":\n");
                Line(source, depth + 3, "{");
                Line(source, depth + 4, "var start = i++;");
                _ = source.Append(' ', (depth + 4) * 4).Append("while (i < records.Count && PortiaHandlerKind(")
                    .Append(ev).Append(") == ").Append(i).Append(")\n");
                Line(source, depth + 5, "i++;");
                Line(source, depth + 4, "var batch = new " + type + "[i - start];");
                Line(source, depth + 4, "for (var index = start; index < i; index++)");
                Line(source, depth + 5, "batch[index - start] = " + (processor.Projector
                    ? "new global::Cntryl.Portia.ProjectorContext<" + handler.Type + ">((" + handler.Type + ")" + itemEvent +
                      ", records[index], context);"
                    : "new global::Cntryl.Portia.ReactorContext<" + handler.Type + ">((" + handler.Type + ")" + itemEvent +
                      ", records[index].Source, records[index]);"));
                _ = source.Append(' ', (depth + 4) * 4)
                    .Append("var result = await global::Cntryl.Portia.ProcessingObservation.InvokeAsync(")
                    .Append(SymbolDisplay.FormatLiteral(handler.InterfaceType, true))
                    .Append(", ((global::Cntryl.Portia.")
                    .Append(processor.Projector ? "IBatchProjectorHandler<" : "IBatchReactorHandler<")
                    .Append(handler.Type).Append(">)this), batch, ")
                    .Append("ct).ConfigureAwait(false);\n");
                Line(source, depth + 4, "if (!result.IsSuccess)");
                Line(source, depth + 5, "return result;");
                Line(source, depth + 4, "break;");
                Line(source, depth + 3, "}");
            }
        }

        Line(source, depth + 3, "default:");
        Line(source, depth + 3, "{");
        if (!processor.Projector)
            Line(source, depth + 4, "var item = records[i++];");
        Line(source, depth + 4, processor.Projector
            ? "var result = await ProjectEventAsync(records[i++], context, ct).ConfigureAwait(false);"
            : "var result = await ReactToEventAsync(item.Source, item, ct).ConfigureAwait(false);");
        Line(source, depth + 4, "if (!result.IsSuccess)");
        Line(source, depth + 5, "return result;");
        Line(source, depth + 4, "break;");
        Line(source, depth + 3, "}");
        Line(source, depth + 2, "}");
        Line(source, depth + 1, "}");
        Line(source, depth + 1, "return global::Cntryl.Portia.Result.Success;");
        Line(source, depth, "}");
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
            parents.Push($"partial {kind} @{parent.Name}");
        }

        return [.. parents];
    }

    static StringBuilder OpenShape(Processor processor)
    {
        var source = new StringBuilder("// <auto-generated />\n#nullable enable\n");
        if (processor.Namespace is not null)
            _ = source.Append("namespace ").Append(processor.Namespace).Append(";\n");
        for (var index = 0; index < processor.Parents.Length; index++)
        {
            Line(source, index, processor.Parents[index]);
            Line(source, index, "{");
        }

        _ = source.Append(' ', processor.Parents.Length * 4)
            .Append("[global::Cntryl.Portia.ProcessorTopologyAttribute(");
        _ = source.Append(string.Join(", ", processor.Handlers.Select(h => SymbolDisplay.FormatLiteral(
            h.DisplayType + (h.Batch ? " [batch]" : " [single]"), true))));
        _ = source.Append(")]\n");
        Line(source, processor.Parents.Length, "partial class @" + processor.Name);
        Line(source, processor.Parents.Length, "{");
        return source;
    }

    static void Line(StringBuilder source, int depth, string text) =>
        source.Append(' ', depth * 4).Append(text).Append('\n');

    static bool InheritsFrom(INamedTypeSymbol symbol, string metadataName)
    {
        for (var current = symbol.BaseType; current is not null; current = current.BaseType)
        {
            if (SymbolNames.Is(current, metadataName))
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
        string? unsupportedReason,
        bool onlyContainersUnsupported,
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
        public string? UnsupportedReason { get; } = unsupportedReason;
        public bool OnlyContainersUnsupported { get; } = onlyContainersUnsupported;
        public bool Partial { get; } = partial;
        public bool Projector { get; } = projector;
        public bool Batch { get; } = batch;
        public Handler[] Handlers { get; } = handlers;

        public Processor WithoutLocation() => new(Name, DisplayName, Namespace, Parents, HintName,
            default, UnsupportedReason, OnlyContainersUnsupported, Partial, Projector, Batch, Handlers);

        public bool Equals(Processor? other) => other is not null && Name == other.Name &&
                                                DisplayName == other.DisplayName && Namespace == other.Namespace &&
                                                HintName == other.HintName &&
                                                Location.Equals(other.Location) &&
                                                UnsupportedReason == other.UnsupportedReason &&
                                                OnlyContainersUnsupported == other.OnlyContainersUnsupported &&
                                                Partial == other.Partial &&
                                                Projector == other.Projector && Batch == other.Batch &&
                                                Parents.SequenceEqual(other.Parents) &&
                                                Handlers.SequenceEqual(other.Handlers);

        public override bool Equals(object? obj) => Equals(obj as Processor);
        public override int GetHashCode() => (Name, DisplayName, Namespace, HintName).GetHashCode();
    }
}
