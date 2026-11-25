using System.Collections.Immutable;
using Cntryl.Portia.CompilerBenchmarks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Cntryl.Portia.Consumer;

public sealed class ComponentPracticeReactionTests
{
    const string Contracts = """
                             using Cntryl.Portia;
                             public sealed record Changed : DomainEvent;
                             namespace Orders
                             {
                                 public sealed record CreateOrder : IRequest;
                                 public sealed record CancelOrder : IRequest;
                                 public sealed record CreateShipment : IRequest;
                                 public sealed record CancelShipment : IRequest;
                             }
                             namespace Archive { public sealed record CancelOrder : IRequest; }
                             """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShouldRespectSeverityGivenRepeatedSendsAndTwoMatchingLifecycles(bool enabled)
    {
        // Arrange
        var options = CompilerHost.CompilationOptions.WithSpecificDiagnosticOptions(
            ImmutableDictionary<string, ReportDiagnostic>.Empty.Add("PORTIA109",
                enabled ? ReportDiagnostic.Warn : ReportDiagnostic.Suppress));
        var creates = Reactor("Start", "Orders.CreateOrder", "Orders.CreateOrder", "Orders.CreateShipment");
        var cancels = Reactor("Undo", "Orders.CancelOrder", "Orders.CancelShipment");

        // Act
        var diagnostics = Analyze(options, ("B.cs", cancels), ("A.cs", creates));

        // Assert
        Assert.Equal(enabled ? 1 : 0, diagnostics.Length);

        if (enabled)
        {
            var diagnostic = Assert.Single(diagnostics);
            Assert.Equal("A.cs", diagnostic.Location.SourceTree!.FilePath);
            Assert.Equal("Start", creates.Substring(diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length));
            Assert.Contains("Orders.CreateOrder", diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ShouldReportAtEnabledDeclarationGivenBothSendsInDisabledFiles()
    {
        // Arrange
        const string declaration = "public sealed partial class Undo { }";
        var options = CompilerHost.CompilationOptions.WithSyntaxTreeOptionsProvider(new PartialFileOptions());

        // Act
        var diagnostics = Analyze(options, ("Start.cs", Reactor("Start", "Orders.CreateOrder")),
            ("Undo.cs", Reactor("Undo", "Orders.CancelOrder")), ("Enabled.cs", declaration));

        // Assert
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("Enabled.cs", diagnostic.Location.SourceTree!.FilePath);
        Assert.Equal("Undo", declaration.Substring(diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length));
    }

    [Fact]
    public void ShouldAcceptUnrelatedReactorsGivenEqualResourceNamesInDifferentNamespaces()
    {
        // Arrange
        var options = CompilerHost.CompilationOptions.WithSpecificDiagnosticOptions(
            ImmutableDictionary<string, ReportDiagnostic>.Empty.Add("PORTIA109", ReportDiagnostic.Warn));

        // Act
        var diagnostics = Analyze(options, ("Start.cs", Reactor("Start", "Orders.CreateOrder")),
            ("Undo.cs", Reactor("Undo", "Archive.CancelOrder")));

        // Assert
        Assert.Empty(diagnostics);
    }

    static string Reactor(string name, params string[] commands) => $$"""
        using Cntryl.Portia;
        using System.Threading;
        using System.Threading.Tasks;
        public sealed partial class {{name}}(IRequestBus bus) : IReactorHandler<Changed>
        {
            public async ValueTask<Result> HandleAsync(IReactorContext<Changed> context, CancellationToken ct)
            {
                {{string.Join("\n", commands.Select(command => $"await bus.SendReactionAsync(new {command}(), context, ct);"))}}
                return Result.Success;
            }
        }
        """;

    static ImmutableArray<Diagnostic> Analyze(CSharpCompilationOptions options, params (string Path, string Source)[] files)
    {
        var trees = files.Select(file => CSharpSyntaxTree.ParseText(file.Source, path: file.Path))
            .Append(CSharpSyntaxTree.ParseText(Contracts, path: "Contracts.cs"));
        var compilation = CSharpCompilation.Create("Reactions", trees, CompilerHost.References, options);
        var ct = TestContext.Current.CancellationToken;
        Assert.DoesNotContain(compilation.GetDiagnostics(ct), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var diagnostics = compilation.WithAnalyzers([new ComponentPracticeAnalyzer()]).GetAnalyzerDiagnosticsAsync(ct)
            .GetAwaiter().GetResult();
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "AD0001");
        return [.. diagnostics.Where(diagnostic => diagnostic.Id == "PORTIA109")];
    }

    sealed class PartialFileOptions : SyntaxTreeOptionsProvider
    {
        public override GeneratedKind IsGenerated(SyntaxTree tree, CancellationToken cancellationToken) => GeneratedKind.Unknown;

        public override bool TryGetDiagnosticValue(SyntaxTree tree, string diagnosticId, CancellationToken cancellationToken, out ReportDiagnostic severity)
        {
            severity = tree.FilePath == "Enabled.cs" ? ReportDiagnostic.Warn : ReportDiagnostic.Suppress;
            return diagnosticId == "PORTIA109";
        }

        public override bool TryGetGlobalDiagnosticValue(string diagnosticId, CancellationToken cancellationToken, out ReportDiagnostic severity)
        {
            severity = ReportDiagnostic.Default;
            return false;
        }
    }
}
