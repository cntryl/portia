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

    [Fact]
    public async Task FixAllCorrectsProjectorAndReactorAndLeavesGeneratedOutputCompilable()
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

        Assert.NotNull(new PartialProcessorCodeFixProvider().GetFixAllProvider());
        var corrected = await ApplyAllAsync(source);

        Assert.Contains("public sealed partial class Projection", corrected, StringComparison.Ordinal);
        Assert.Contains("internal partial class Reaction", corrected, StringComparison.Ordinal);
        Assert.DoesNotContain(GeneratorCompilation.Diagnostics(corrected,
            new ProjectorReactorEventDispatcherGenerator()), diagnostic =>
            diagnostic.Id is "PORTIA002" or "PORTIA005");
        _ = GeneratorCompilation.Compile(corrected, new ProjectorReactorEventDispatcherGenerator());
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
