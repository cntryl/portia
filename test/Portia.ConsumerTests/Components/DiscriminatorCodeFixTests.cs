using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Cntryl.Portia.Consumer;

public sealed class DiscriminatorCodeFixTests
{
    static readonly IIncrementalGenerator[] Generators =
        [new PortiaServiceRegistrationGenerator(), new DomainEventCatalogGenerator()];

    [Fact]
    public async Task AddsDiscriminatorAfterExistingAttributesOnATransportedRequest()
    {
        const string source = """
                              using Cntryl.Portia;
                              [RequestRoute("*", "orders", "order", "create")]
                              public sealed record CreateOrder : IRequest, IQueuable;
                              """;

        var corrected = await ApplyAllAsync(source, "PORTIA020");

        AssertContains(
            "[RequestRoute(\"*\", \"orders\", \"order\", \"create\")]\n[Discriminator(\"CreateOrder\")]\npublic sealed record CreateOrder",
            corrected, StringComparison.Ordinal);
        AssertRepaired(corrected);
    }

    [Fact]
    public async Task AddsDiscriminatorBelowDocumentationOnAnUnattributedEvent()
    {
        const string source = """
                              using Cntryl.Portia;
                              /// <summary>An account was opened.</summary>
                              public sealed record Opened : DomainEvent;
                              """;

        var corrected = await ApplyAllAsync(source, "PORTIA021");

        AssertContains(
            "/// <summary>An account was opened.</summary>\n[Discriminator(\"Opened\")]\npublic sealed record Opened : DomainEvent;",
            corrected, StringComparison.Ordinal);
        AssertRepaired(corrected);
    }

    [Fact]
    public async Task KeepsTheIndentationOfANestedEvent()
    {
        const string source = """
                              using Cntryl.Portia;
                              public static class Events
                              {
                                  public sealed record Opened : DomainEvent;
                              }
                              """;

        var corrected = await ApplyAllAsync(source, "PORTIA021");

        AssertContains("{\n    [Discriminator(\"Opened\")]\n    public sealed record Opened : DomainEvent;\n}", corrected,
            StringComparison.Ordinal);
        AssertRepaired(corrected);
    }

    [Theory]
    [InlineData("[Discriminator(\"\")]", "[Discriminator(\"Opened\")]")]
    [InlineData("[Discriminator(\"orders.opened\", 0)]", "[Discriminator(\"orders.opened\", 1)]")]
    [InlineData("[Discriminator(name: \"orders.opened\", 0)]", "[Discriminator(name: \"orders.opened\", 1)]")]
    [InlineData("[Discriminator(\" \", -3)]", "[Discriminator(\"Opened\", 1)]")]
    public async Task RepairsOnlyTheInvalidArgumentsOfAnExistingAttribute(string attribute, string expected)
    {
        var corrected = await ApplyAllAsync($"using Cntryl.Portia;\n{attribute}\npublic sealed record Opened : DomainEvent;",
            "PORTIA021");

        AssertContains($"{expected}\npublic sealed record Opened : DomainEvent;", corrected, StringComparison.Ordinal);
        Assert.DoesNotContain(attribute, corrected, StringComparison.Ordinal);
        AssertRepaired(corrected);
    }

    [Fact]
    public async Task QualifiesTheAttributeWhenPortiaIsNotImported()
    {
        const string source = "public sealed record Opened : Cntryl.Portia.DomainEvent;";

        var corrected = await ApplyAllAsync(source, "PORTIA021");

        AssertContains("[global::Cntryl.Portia.Discriminator(\"Opened\")]\npublic sealed record Opened", corrected,
            StringComparison.Ordinal);
        AssertRepaired(corrected);
    }

    [Fact]
    public async Task QualifiesTheAttributeWhenAnotherImportedAttributeHasTheSameName()
    {
        const string source = """
                              using Cntryl.Portia;
                              using Other;
                              namespace Other
                              {
                                  public sealed class DiscriminatorAttribute : System.Attribute { }
                              }
                              public sealed record Opened : DomainEvent;
                              """;

        var corrected = await ApplyAllAsync(source, "PORTIA021");

        AssertContains("[global::Cntryl.Portia.Discriminator(\"Opened\")]\npublic sealed record Opened", corrected,
            StringComparison.Ordinal);
        AssertRepaired(corrected);
    }

    [Theory]
    [InlineData(FixAllScope.Document)]
    [InlineData(FixAllScope.Project)]
    [InlineData(FixAllScope.Solution)]
    public async Task FixAllDeclaresEveryMissingDiscriminatorAndLeavesGeneratedOutputCompilable(FixAllScope scope)
    {
        const string source = """
                              using Cntryl.Portia;
                              [RequestRoute("*", "orders", "order", "create")]
                              public sealed record CreateOrder : IRequest, IQueuable;
                              public sealed record Opened : DomainEvent;
                              """;

        var corrected = await ApplyFixAllAsync(source, scope, "Portia.AddDiscriminator");

        AssertContains("[Discriminator(\"CreateOrder\")]\npublic sealed record CreateOrder", corrected,
            StringComparison.Ordinal);
        AssertContains("[Discriminator(\"Opened\")]\npublic sealed record Opened", corrected, StringComparison.Ordinal);
        AssertRepaired(corrected);
        Assert.Equal(corrected, await ApplyFixAllAsync(corrected, scope, "Portia.AddDiscriminator"));
    }

    // xunit truncates the compared strings; the whole corrected document is what explains a layout failure.
    static void AssertContains(string expected, string corrected, StringComparison comparison) =>
        Assert.True(corrected.Contains(expected, comparison),
            $"Expected to find:\n{expected}\n\nin:\n{corrected}");

    // The corrected declaration must satisfy both generators and still be valid C# on its own.
    static void AssertRepaired(string corrected)
    {
        Assert.DoesNotContain(GeneratorCompilation.Diagnostics(corrected, Generators),
            diagnostic => diagnostic.Id is "PORTIA020" or "PORTIA021");
        Assert.DoesNotContain(GeneratorCompilation.WarningsAsErrorsDiagnostics(corrected),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    static async Task<string> ApplyFixAllAsync(string source, FixAllScope scope, string equivalenceKey)
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("FixAll", LanguageNames.CSharp)
            .WithParseOptions(new CSharpParseOptions(LanguageVersion.Preview))
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddMetadataReferences(References());
        Assert.True(workspace.TryApplyChanges(project.Solution));
        var document = workspace.AddDocument(project.Id, "Contracts.cs", SourceText.From(source));
        // Project and solution runs have another document; solution also includes another project.
        if (scope != FixAllScope.Document)
        {
            _ = workspace.AddDocument(project.Id, "Other.cs", SourceText.From(source
                .Replace("CreateOrder", "OtherCreateOrder").Replace("Opened", "OtherOpened")
                .Replace("\"create\"", "\"other\"")));
        }

        if (scope == FixAllScope.Solution)
        {
            var other = workspace.AddProject("OtherProject", LanguageNames.CSharp)
                .WithParseOptions(project.ParseOptions!).WithCompilationOptions(project.CompilationOptions!)
                .AddMetadataReferences(project.MetadataReferences);
            Assert.True(workspace.TryApplyChanges(other.Solution));
            _ = workspace.AddDocument(other.Id, "Contracts.cs", SourceText.From(source));
        }

        document = workspace.CurrentSolution.GetDocument(document.Id)!;
        var provider = new DiscriminatorCodeFixProvider();
        var context = new FixAllContext(document, provider, scope, equivalenceKey, provider.FixableDiagnosticIds,
            new DiscriminatorDiagnosticProvider(), CancellationToken.None);
        var action = await provider.GetFixAllProvider().GetFixAsync(context);
        if (action is null)
            return source;
        var operations = await action.GetOperationsAsync(CancellationToken.None);
        var solution = Assert.Single(operations.OfType<ApplyChangesOperation>()).ChangedSolution;
        foreach (var changed in solution.Projects.SelectMany(item => item.Documents))
            AssertRepaired((await changed.GetTextAsync()).ToString());
        return (await solution.GetDocument(document.Id)!.GetTextAsync()).ToString();
    }

    sealed class DiscriminatorDiagnosticProvider : FixAllContext.DiagnosticProvider
    {
        public override async Task<IEnumerable<Diagnostic>> GetDocumentDiagnosticsAsync(Document document,
            CancellationToken cancellationToken)
        {
            var compilation = await document.Project.GetCompilationAsync(cancellationToken);
            var tree = await document.GetSyntaxTreeAsync(cancellationToken);
            GeneratorDriver driver = CSharpGeneratorDriver.Create(
                Generators.Select(generator => generator.AsSourceGenerator()),
                parseOptions: (CSharpParseOptions)document.Project.ParseOptions!);
            driver = driver.RunGenerators(compilation!, cancellationToken);
            // Portable generator diagnostics deliberately carry external-file locations. The IDE
            // associates them with the workspace tree before invoking its document-based fixer.
            return driver.GetRunResult().Diagnostics.Where(diagnostic =>
                    diagnostic.Location.GetLineSpan().Path == tree!.FilePath
                    && diagnostic.Id is "PORTIA020" or "PORTIA021")
                .Select(diagnostic => Diagnostic.Create(diagnostic.Descriptor,
                    Location.Create(tree!, diagnostic.Location.SourceSpan), diagnostic.Properties));
        }

        public override Task<IEnumerable<Diagnostic>> GetProjectDiagnosticsAsync(Project project,
            CancellationToken cancellationToken) =>
            Task.FromResult(Enumerable.Empty<Diagnostic>());

        public override async Task<IEnumerable<Diagnostic>> GetAllDiagnosticsAsync(Project project,
            CancellationToken cancellationToken)
        {
            var diagnostics = new List<Diagnostic>();
            foreach (var document in project.Documents)
                diagnostics.AddRange(await GetDocumentDiagnosticsAsync(document, cancellationToken));
            return diagnostics;
        }
    }

    static async Task<string> ApplyAllAsync(string source, string id)
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("CodeFix", LanguageNames.CSharp)
            .WithParseOptions(new CSharpParseOptions(LanguageVersion.Preview))
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddMetadataReferences(References());
        // The fix reads the attribute through the semantic model, so the references must reach the workspace.
        Assert.True(workspace.TryApplyChanges(project.Solution));
        var document = workspace.AddDocument(project.Id, "Contracts.cs", SourceText.From(source));
        var diagnostics = GeneratorCompilation.Diagnostics(source, Generators)
            .Where(diagnostic => diagnostic.Id == id)
            .OrderByDescending(diagnostic => diagnostic.Location.SourceSpan.Start).ToArray();
        Assert.NotEmpty(diagnostics);

        foreach (var diagnostic in diagnostics)
        {
            var actions = new List<CodeAction>();
            var provider = new DiscriminatorCodeFixProvider();
            await provider.RegisterCodeFixesAsync(new CodeFixContext(document, diagnostic,
                (action, _) => actions.Add(action), CancellationToken.None));
            var action = Assert.Single(actions);
            var operations = await action.GetOperationsAsync(CancellationToken.None);
            var apply = Assert.Single(operations.OfType<ApplyChangesOperation>());
            document = apply.ChangedSolution.GetDocument(document.Id)!;
        }

        return (await document.GetTextAsync()).ToString();
    }

    static IEnumerable<MetadataReference> References() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator)
        .Append(typeof(Aggregate).Assembly.Location)
        .Append(typeof(IRemoteRequestSender).Assembly.Location)
        .Distinct(StringComparer.Ordinal)
        .Select(path => MetadataReference.CreateFromFile(path));
}
