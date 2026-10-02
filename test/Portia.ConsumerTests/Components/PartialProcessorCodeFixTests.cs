using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Cntryl.Portia.Consumer;

public sealed class PartialProcessorCodeFixTests
{
    [Fact]
    public async Task AddsPartialWithoutDisturbingExistingModifiersOrTrivia()
    {
        const string source = """
                              using Cntryl.Portia;
                              public sealed record Changed : DomainEvent;
                              // processor documentation
                              public /* retained */ sealed class Projection
                                  : Projector(null!, EventStreamPattern.ForPattern("events")), IProjectorHandler<Changed>;
                              """;

        var corrected = await ApplyAllAsync(source);

        Assert.Contains("// processor documentation", corrected, StringComparison.Ordinal);
        Assert.Contains("public /* retained */ sealed partial class Projection", corrected, StringComparison.Ordinal);
        Assert.DoesNotContain(GeneratorCompilation.Diagnostics(corrected,
            new ProjectorReactorEventDispatcherGenerator()), diagnostic => diagnostic.Id == "PORTIA002");
    }

    [Theory]
    [InlineData("/// docs\nclass Projection() : Projector(null!, EventStreamPattern.ForPattern(\"events\"));",
        "/// docs\npartial class Projection")]
    [InlineData("    class Projection() : Projector(null!, EventStreamPattern.ForPattern(\"events\"));",
        "    partial class Projection")]
    [InlineData("[System.Obsolete]\nclass Projection() : Projector(null!, EventStreamPattern.ForPattern(\"events\"));",
        "[System.Obsolete]\npartial class Projection")]
    public async Task ModifierFreeDeclarationPreservesTriviaOnItsActualFirstToken(string declaration,
        string expected)
    {
        var corrected = await ApplyAllAsync("using Cntryl.Portia;\npublic sealed record Changed : DomainEvent;\n" +
                                            declaration.Replace(";", ", IProjectorHandler<Changed>;"));

        Assert.Contains(expected, corrected, StringComparison.Ordinal);
        Assert.DoesNotContain(GeneratorCompilation.Diagnostics(corrected,
                new ProjectorReactorEventDispatcherGenerator()),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Theory]
    [InlineData(FixAllScope.Document)]
    [InlineData(FixAllScope.Project)]
    [InlineData(FixAllScope.Solution)]
    public async Task FixAllCorrectsProjectorAndReactorAndLeavesGeneratedOutputCompilable(FixAllScope scope)
    {
        const string source = """
                              using Cntryl.Portia;
                              using System.Threading;
                              using System.Threading.Tasks;
                              public sealed record Changed : DomainEvent;
                              public sealed class Projection()
                                  : Projector(null!, EventStreamPattern.ForPattern("events")), IProjectorHandler<Changed>
                              {
                                  public ValueTask HandleAsync(Changed ev, IProjectorContext context, CancellationToken ct) => ValueTask.CompletedTask;
                              }
                              internal class Reaction()
                                  : Reactor(null!, EventStreamPattern.ForPattern("events")), IReactorHandler<Changed>
                              {
                                  public ValueTask HandleAsync(IReactorContext<Changed> context, CancellationToken ct) => ValueTask.CompletedTask;
                              }
                              """;

        var corrected = await ApplyFixAllAsync(source, scope, "Portia.MakeProcessorPartial");

        Assert.Contains("public sealed partial class Projection", corrected, StringComparison.Ordinal);
        Assert.Contains("internal partial class Reaction", corrected, StringComparison.Ordinal);
        Assert.DoesNotContain(GeneratorCompilation.Diagnostics(corrected,
            new ProjectorReactorEventDispatcherGenerator()), diagnostic =>
            diagnostic.Id is "PORTIA002" or "PORTIA005");
        _ = GeneratorCompilation.Compile(corrected, new ProjectorReactorEventDispatcherGenerator());
        Assert.Equal(corrected, await ApplyFixAllAsync(corrected, scope, "Portia.MakeProcessorPartial"));
    }

    [Fact]
    public async Task MakesEveryEnclosingTypeOfANestedProcessorPartial()
    {
        const string source = """
                              using Cntryl.Portia;
                              using System.Threading;
                              using System.Threading.Tasks;
                              public sealed record Changed : DomainEvent;
                              public static class Outer
                              {
                                  // nested
                                  internal sealed class Middle
                                  {
                                      internal sealed partial class Projection()
                                          : Projector(null!, EventStreamPattern.ForPattern("events")), IProjectorHandler<Changed>
                                      {
                                          public ValueTask HandleAsync(Changed ev, IProjectorContext context, CancellationToken ct) => ValueTask.CompletedTask;
                                      }
                                  }
                              }
                              """;

        var corrected = await ApplyAllAsync(source, "PORTIA015");

        Assert.Contains("public static partial class Outer", corrected, StringComparison.Ordinal);
        Assert.Contains("    // nested\n    internal sealed partial class Middle", corrected, StringComparison.Ordinal);
        Assert.DoesNotContain(GeneratorCompilation.Diagnostics(corrected,
            new ProjectorReactorEventDispatcherGenerator()), diagnostic => diagnostic.Id == "PORTIA015");
        _ = GeneratorCompilation.Compile(corrected, new ProjectorReactorEventDispatcherGenerator());
    }

    [Fact]
    public async Task OffersNoPartialFixForAnUnsupportedShapeItCannotRepair()
    {
        const string source = """
                              using Cntryl.Portia;
                              using System.Threading;
                              using System.Threading.Tasks;
                              public sealed record Changed : DomainEvent;
                              public static partial class Outer
                              {
                                  private sealed partial class Projection()
                                      : Projector(null!, EventStreamPattern.ForPattern("events")), IProjectorHandler<Changed>
                                  {
                                      public ValueTask HandleAsync(Changed ev, IProjectorContext context, CancellationToken ct) => ValueTask.CompletedTask;
                                  }
                              }
                              """;

        Assert.Empty(await ActionsAsync(source, "PORTIA015"));
    }

    [Theory]
    [InlineData(FixAllScope.Document)]
    [InlineData(FixAllScope.Project)]
    [InlineData(FixAllScope.Solution)]
    public async Task FixAllRepairsSharedNestedContainers(FixAllScope scope)
    {
        const string source = """
                              using Cntryl.Portia;
                              using System.Threading;
                              using System.Threading.Tasks;
                              public sealed record Changed : DomainEvent;
                              // shared parent
                              public static class Outer
                              {
                                  internal class Middle
                                  {
                                      internal partial class Projection() : Projector(null!, EventStreamPattern.ForPattern("events")), IProjectorHandler<Changed>
                                      {
                                          public ValueTask HandleAsync(Changed ev, IProjectorContext context, CancellationToken ct) => ValueTask.CompletedTask;
                                      }
                                      internal partial class Reaction() : Reactor(null!, EventStreamPattern.ForPattern("events")), IReactorHandler<Changed>
                                      {
                                          public ValueTask HandleAsync(IReactorContext<Changed> context, CancellationToken ct) => ValueTask.CompletedTask;
                                      }
                                  }
                              }
                              """;
        var corrected = await ApplyFixAllAsync(source, scope, "Portia.MakeContainersPartial");
        Assert.Contains("// shared parent", corrected, StringComparison.Ordinal);
        Assert.Contains("public static partial class Outer", corrected, StringComparison.Ordinal);
        Assert.Contains("internal partial class Middle", corrected, StringComparison.Ordinal);
        _ = GeneratorCompilation.Compile(corrected, new ProjectorReactorEventDispatcherGenerator());
        Assert.Equal(corrected, await ApplyFixAllAsync(corrected, scope, "Portia.MakeContainersPartial"));
    }

    static async Task<string> ApplyFixAllAsync(string source, FixAllScope scope, string equivalenceKey)
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("FixAll", LanguageNames.CSharp)
            .WithParseOptions(new CSharpParseOptions(LanguageVersion.Preview))
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddMetadataReferences(((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
                .Split(Path.PathSeparator).Append(typeof(Aggregate).Assembly.Location)
                .Distinct(StringComparer.Ordinal).Select(path => MetadataReference.CreateFromFile(path)));
        Assert.True(workspace.TryApplyChanges(project.Solution));
        var document = workspace.AddDocument(project.Id, "Processor.cs", SourceText.From(source));
        // Project and solution runs have another document; solution also includes another project.
        if (scope != FixAllScope.Document)
            _ = workspace.AddDocument(project.Id, "Other.cs", SourceText.From(source.Replace("Outer", "OtherOuter").Replace("Projection", "OtherProjection").Replace("Reaction", "OtherReaction").Replace("Changed", "OtherChanged")));
        if (scope == FixAllScope.Solution)
        {
            var other = workspace.AddProject("OtherProject", LanguageNames.CSharp)
                .WithParseOptions(project.ParseOptions!).WithCompilationOptions(project.CompilationOptions!)
                .AddMetadataReferences(project.MetadataReferences);
            Assert.True(workspace.TryApplyChanges(other.Solution));
            _ = workspace.AddDocument(other.Id, "Processor.cs", SourceText.From(source));
        }
        document = workspace.CurrentSolution.GetDocument(document.Id)!;
        var provider = new PartialProcessorCodeFixProvider();
        var context = new FixAllContext(document, provider, scope, equivalenceKey, provider.FixableDiagnosticIds,
            new ProcessorDiagnosticProvider(), CancellationToken.None);
        var action = await provider.GetFixAllProvider().GetFixAsync(context);
        if (action is null)
            return source;
        var operations = await action.GetOperationsAsync(CancellationToken.None);
        var solution = Assert.Single(operations.OfType<ApplyChangesOperation>()).ChangedSolution;
        foreach (var changed in solution.Projects.SelectMany(item => item.Documents))
        {
            var text = (await changed.GetTextAsync()).ToString();
            Assert.DoesNotContain(GeneratorCompilation.Diagnostics(text, new ProjectorReactorEventDispatcherGenerator()),
                diagnostic => diagnostic.Id is "PORTIA002" or "PORTIA005" or "PORTIA015");
            _ = GeneratorCompilation.Compile(text, new ProjectorReactorEventDispatcherGenerator());
        }
        return (await solution.GetDocument(document.Id)!.GetTextAsync()).ToString();
    }

    sealed class ProcessorDiagnosticProvider : FixAllContext.DiagnosticProvider
    {
        public override async Task<IEnumerable<Diagnostic>> GetDocumentDiagnosticsAsync(Document document, CancellationToken cancellationToken)
        {
            var compilation = await document.Project.GetCompilationAsync(cancellationToken);
            var tree = await document.GetSyntaxTreeAsync(cancellationToken);
            GeneratorDriver driver = CSharpGeneratorDriver.Create(
                [new ProjectorReactorEventDispatcherGenerator().AsSourceGenerator()],
                parseOptions: (CSharpParseOptions)document.Project.ParseOptions!);
            driver = driver.RunGenerators(compilation!, cancellationToken);
            // Portable generator diagnostics deliberately carry external-file locations. The IDE
            // associates them with the workspace tree before invoking its document-based fixer.
            return driver.GetRunResult().Diagnostics.Where(diagnostic =>
                    diagnostic.Location.GetLineSpan().Path == tree!.FilePath
                    && diagnostic.Id is "PORTIA002" or "PORTIA005" or "PORTIA015")
                .Select(diagnostic => Diagnostic.Create(diagnostic.Descriptor,
                    Location.Create(tree!, diagnostic.Location.SourceSpan), diagnostic.Properties));
        }

        public override Task<IEnumerable<Diagnostic>> GetProjectDiagnosticsAsync(Project project, CancellationToken cancellationToken) =>
            Task.FromResult(Enumerable.Empty<Diagnostic>());

        public override async Task<IEnumerable<Diagnostic>> GetAllDiagnosticsAsync(Project project, CancellationToken cancellationToken)
        {
            var diagnostics = new List<Diagnostic>();
            foreach (var document in project.Documents)
                diagnostics.AddRange(await GetDocumentDiagnosticsAsync(document, cancellationToken));
            return diagnostics;
        }
    }

    static async Task<IReadOnlyList<CodeAction>> ActionsAsync(string source, string id)
    {
        using var workspace = new AdhocWorkspace();
        var document = workspace.AddDocument(workspace.AddProject("CodeFix", LanguageNames.CSharp).Id, "Processor.cs",
            SourceText.From(source));
        var actions = new List<CodeAction>();
        foreach (var diagnostic in GeneratorCompilation.Diagnostics(source,
                     new ProjectorReactorEventDispatcherGenerator()).Where(diagnostic => diagnostic.Id == id))
        {
            await new PartialProcessorCodeFixProvider().RegisterCodeFixesAsync(new CodeFixContext(document,
                diagnostic, (action, _) => actions.Add(action), CancellationToken.None));
        }

        return actions;
    }

    static async Task<string> ApplyAllAsync(string source, string? id = null)
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("CodeFix", LanguageNames.CSharp)
            .WithParseOptions(new CSharpParseOptions(LanguageVersion.Preview))
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddMetadataReferences(((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
                .Split(Path.PathSeparator).Append(typeof(Aggregate).Assembly.Location)
                .Distinct(StringComparer.Ordinal).Select(path => MetadataReference.CreateFromFile(path)));
        var document = workspace.AddDocument(project.Id, "Processor.cs", SourceText.From(source));
        var diagnostics = GeneratorCompilation.Diagnostics(source,
                new ProjectorReactorEventDispatcherGenerator())
            .Where(diagnostic => id is null ? diagnostic.Id is "PORTIA002" or "PORTIA005" : diagnostic.Id == id)
            .OrderByDescending(diagnostic => diagnostic.Location.SourceSpan.Start).ToArray();

        foreach (var diagnostic in diagnostics)
        {
            var actions = new List<CodeAction>();
            var provider = new PartialProcessorCodeFixProvider();
            await provider.RegisterCodeFixesAsync(new CodeFixContext(document, diagnostic,
                (action, _) => actions.Add(action), CancellationToken.None));
            var action = Assert.Single(actions);
            var operations = await action.GetOperationsAsync(CancellationToken.None);
            var apply = Assert.Single(operations.OfType<ApplyChangesOperation>());
            document = apply.ChangedSolution.GetDocument(document.Id)!;
        }

        return (await document.GetTextAsync()).ToString();
    }
}
