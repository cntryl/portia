using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Cntryl.Portia;

public sealed partial class ComponentPracticeAnalyzer
{
    static void RegisterSplitReactions(CompilationStartAnalysisContext start)
    {
        var options = start.Compilation.Options;
        var id = SplitDependentReactions.Id;
        var treeOptions = options.SyntaxTreeOptionsProvider;
        var configured = options.SpecificDiagnosticOptions.TryGetValue(id, out var severity)
                         && severity != ReportDiagnostic.Default;
        var global = treeOptions is not null
                     && treeOptions.TryGetGlobalDiagnosticValue(id, start.CancellationToken, out var value)
                     && value != ReportDiagnostic.Suppress;

        bool Enabled(SyntaxTree tree, CancellationToken ct)
        {
            if (configured)
                return severity != ReportDiagnostic.Suppress;

            return treeOptions is not null && treeOptions.TryGetDiagnosticValue(tree, id, ct, out var perTree)
                ? perTree != ReportDiagnostic.Suppress
                : global;
        }

        if (configured ? severity == ReportDiagnostic.Suppress : treeOptions is null ||
            !start.Compilation.SyntaxTrees.Any(tree => Enabled(tree, start.CancellationToken)))
            return;

        var index = new ReactionIndex();
        start.RegisterSyntaxNodeAction(index.Collect, SyntaxKind.InvocationExpression);
        start.RegisterCompilationEndAction(end => index.Report(end, Enabled));
    }

    sealed class Lifecycle
    {
        public Dictionary<INamedTypeSymbol, INamedTypeSymbol> Creates { get; } = [with(SymbolEqualityComparer.Default)];
        public Dictionary<INamedTypeSymbol, INamedTypeSymbol> Cancels { get; } = [with(SymbolEqualityComparer.Default)];
    }

    sealed class ReactionIndex
    {
        readonly Dictionary<INamespaceSymbol, Dictionary<string, Lifecycle>> _byNamespace =
            [with(SymbolEqualityComparer.Default)];

        public void Collect(SyntaxNodeAnalysisContext context)
        {
            var invocation = (InvocationExpressionSyntax)context.Node;
            var name = invocation.Expression switch
            {
                MemberAccessExpressionSyntax member => member.Name,
                MemberBindingExpressionSyntax member => member.Name,
                SimpleNameSyntax simple => simple,
                _ => null
            };
            var ct = context.CancellationToken;
            var model = context.SemanticModel;

            if (name?.Identifier.ValueText != "SendReactionAsync"
                || model.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol target || !IsReactionDispatch(target)
                || invocation.FirstAncestorOrSelf<TypeDeclarationSyntax>() is not { } declaration
                || model.GetDeclaredSymbol(declaration, ct) is not { IsAbstract: false } reactor
                || invocation.FirstAncestorOrSelf<MethodDeclarationSyntax>() is not { } method
                || model.GetDeclaredSymbol(method, ct) is not { } handler
                || !IsReactorHandlerImplementation(reactor, handler))
                return;

            var command = invocation.ArgumentList.Arguments.Select(argument => model.GetTypeInfo(argument.Expression, ct).Type)
                .OfType<INamedTypeSymbol>().FirstOrDefault(ApplicationProject.IsRequest);

            if (command is null)
                return;

            var creates = command.Name.StartsWith("Create", StringComparison.Ordinal);

            if ((!creates && !command.Name.StartsWith("Cancel", StringComparison.Ordinal)) || command.Name.Length == 6)
                return;

            var resource = command.Name.Substring(6);
            lock (_byNamespace)
            {
                if (!_byNamespace.TryGetValue(command.ContainingNamespace, out var resources))
                    _byNamespace.Add(command.ContainingNamespace, resources = [with(StringComparer.Ordinal)]);

                if (!resources.TryGetValue(resource, out var lifecycle))
                    resources.Add(resource, lifecycle = new Lifecycle());

                var sends = creates ? lifecycle.Creates : lifecycle.Cancels;

                if (!sends.TryGetValue(reactor, out var previous) || (!SymbolEqualityComparer.Default.Equals(previous, command)
                    && string.CompareOrdinal(previous.ToDisplayString(), command.ToDisplayString()) > 0))
                    sends[reactor] = command;
            }
        }

        public void Report(CompilationAnalysisContext context, Func<SyntaxTree, CancellationToken, bool> enabled)
        {
            var ct = context.CancellationToken;
            var reported = new HashSet<(int, int)>();
            var reactors = new Dictionary<INamedTypeSymbol, (int Id, Location? Location)>(SymbolEqualityComparer.Default);

            (int Id, Location? Location) Reactor(INamedTypeSymbol type)
            {
                if (!reactors.TryGetValue(type, out var reactor))
                {
                    var location = type.Locations.Where(loc => loc.SourceTree is { } tree && enabled(tree, ct))
                        .OrderBy(loc => loc.SourceTree!.FilePath, StringComparer.Ordinal)
                        .ThenBy(loc => loc.SourceSpan.Start).FirstOrDefault();
                    reactors.Add(type, reactor = (reactors.Count, location));
                }

                return reactor;
            }

            // A pair may match several resources. Choose the same message and declaration on every run.
            foreach (var space in _byNamespace.OrderBy(pair => pair.Key.ToDisplayString(), StringComparer.Ordinal)
                         .ThenBy(pair => pair.Key.ContainingAssembly?.Identity.ToString(), StringComparer.Ordinal))
                foreach (var entry in space.Value.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    ct.ThrowIfCancellationRequested();
                    var lifecycle = entry.Value;

                    if (lifecycle.Creates.Count == 0 || lifecycle.Cancels.Count == 0)
                        continue;

                    var cancels = lifecycle.Cancels.OrderBy(pair => pair.Key.ToDisplayString(), StringComparer.Ordinal).ToArray();

                    foreach (var create in lifecycle.Creates.OrderBy(pair => pair.Key.ToDisplayString(), StringComparer.Ordinal))
                        foreach (var cancel in cancels)
                        {
                            ct.ThrowIfCancellationRequested();
                            var left = Reactor(create.Key);
                            var right = Reactor(cancel.Key);

                            if (left.Id == right.Id || (left.Location is null && right.Location is null)
                                || !reported.Add(left.Id < right.Id ? (left.Id, right.Id) : (right.Id, left.Id)))
                                continue;

                            var order = string.CompareOrdinal(left.Location?.SourceTree?.FilePath, right.Location?.SourceTree?.FilePath);
                            var createFirst = right.Location is null || (left.Location is not null &&
                                (order < 0 || (order == 0 && left.Location.SourceSpan.Start <= right.Location.SourceSpan.Start)));
                            var (first, second) = createFirst ? (create, cancel) : (cancel, create);
                            context.ReportDiagnostic(Diagnostic.Create(SplitDependentReactions,
                                createFirst ? left.Location : right.Location, first.Key.Name,
                                first.Value.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat), second.Key.Name,
                                second.Value.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat), entry.Key));
                        }
                }
        }
    }
}
