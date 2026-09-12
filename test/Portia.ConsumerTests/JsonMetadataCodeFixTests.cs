using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Cntryl.Portia.Consumer;

/// <summary>
///     Verifies the code fix for PORTIA025, the diagnostic a consumer meets when a request, result, or
///     event type is not reachable from the application's JSON context. The fix is what turns that
///     diagnostic into a one-keystroke correction, so it has to put the root in the right context —
///     and offer to create one when the project has none at all.
/// </summary>
public sealed class JsonMetadataCodeFixTests
{
    const string UnrootedRequest = """
                                   using System.Threading;
                                   using System.Threading.Tasks;
                                   using Cntryl.Portia;
                                   namespace App;
                                   public sealed record Query : IRequest<Answer>;
                                   public sealed record Answer;
                                   public sealed class Handler : IRequestHandler<Query, Answer>
                                   {
                                       public ValueTask<Result<Answer>> HandleAsync(IRequestContext<Query> context, CancellationToken ct) => default;
                                   }
                                   """;

    // The multi-context cases need sibling namespaces, which a file-scoped namespace cannot express.
    const string UnrootedRequestInBlockNamespace = """
                                                   using System.Threading;
                                                   using System.Threading.Tasks;
                                                   using Cntryl.Portia;
                                                   namespace App
                                                   {
                                                       public sealed record Query : IRequest<Answer>;
                                                       public sealed record Answer;
                                                       public sealed class Handler : IRequestHandler<Query, Answer>
                                                       {
                                                           public ValueTask<Result<Answer>> HandleAsync(IRequestContext<Query> context, CancellationToken ct) => default;
                                                       }
                                                   }
                                                   """;

    /// <summary>
    ///     Verifies that a project with no application JSON context at all is offered one, pre-seeded with
    ///     the missing root — the state every new application starts in, where "add it to your context" is
    ///     useless advice because there is no context yet.
    /// </summary>
    [Fact]
    public async Task OffersToCreateAContextWhenTheProjectHasNone()
    {
        var (actions, solution) = await FixAsync(UnrootedRequest, "App.Query", projectName: "orders-api");

        var action = Assert.Single(actions,
            item => item.Title.StartsWith("Create a Portia JSON context", StringComparison.Ordinal));
        var created = await ApplyAsync(solution, action);
        var document = Assert.Single(created.Projects.Single().Documents,
            item => item.Name == "PortiaJsonContext.cs");
        var text = (await document.GetTextAsync()).ToString();
        Assert.Contains("namespace App;", text, StringComparison.Ordinal);
        Assert.Contains("[PortiaJsonContext]", text, StringComparison.Ordinal);
        Assert.Contains("[JsonSerializable(typeof(App.Query))]", text, StringComparison.Ordinal);
        Assert.Contains("JsonSerializerContext", text, StringComparison.Ordinal);
        Assert.DoesNotContain(CSharpSyntaxTree.ParseText(text).GetDiagnostics(),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task CreatesAUniquelyNamedContextWhenPortiaJsonContextIsAlreadyTaken()
    {
        var source = UnrootedRequest + """

                                       internal sealed class PortiaJsonContext;
                                       """;
        var (actions, solution) = await FixAsync(source, "App.Query");

        var created = await ApplyAsync(solution, Assert.Single(actions));
        var document = Assert.Single(created.Projects.Single().Documents,
            item => item.Name == "ApplicationJsonContext.cs");
        var text = (await document.GetTextAsync()).ToString();
        Assert.Contains("internal sealed partial class ApplicationJsonContext", text, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Verifies that when the application has exactly one JSON context, the missing root is added to it
    ///     without disturbing the roots already declared there.
    /// </summary>
    [Fact]
    public async Task AddsTheMissingRootToTheOnlyApplicationContext()
    {
        var source = UnrootedRequest + """

                                       [PortiaJsonContext]
                                       [System.Text.Json.Serialization.JsonSerializable(typeof(Answer))]
                                       internal sealed partial class AppJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
                                       """;

        var (actions, solution) = await FixAsync(source, "App.Query");

        var action = Assert.Single(actions);
        Assert.Equal("Add global::App.Query to AppJsonContext", action.Title);
        var text = await SingleDocumentTextAsync(await ApplyAsync(solution, action));
        Assert.Contains("typeof(App.Query)", text, StringComparison.Ordinal);
        Assert.Contains("typeof(Answer)", text, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Verifies that a root already declared is left exactly as it is, so applying the fix twice — or
    ///     a fix-all that revisits a context — never accumulates duplicate attributes.
    /// </summary>
    [Fact]
    public async Task LeavesTheContextUnchangedWhenTheRootIsAlreadyDeclared()
    {
        var source = UnrootedRequest + """

                                       [PortiaJsonContext]
                                       [System.Text.Json.Serialization.JsonSerializable(typeof(Answer))]
                                       internal sealed partial class AppJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
                                       """;
        var (actions, solution) = await FixAsync(source, "App.Query");
        var once = await ApplyAsync(solution, Assert.Single(actions));

        // Re-running the same fix over the already-corrected document is exactly what a fix-all that
        // revisits one context does; it must not append the root a second time.
        var repeated = await FixAsync(await SingleDocumentTextAsync(once), "App.Query", expectDiagnostic: false);
        var twice = repeated.Actions.Count == 0
            ? once
            : await ApplyAsync(once, repeated.Actions[0]);

        Assert.Equal(await SingleDocumentTextAsync(once), await SingleDocumentTextAsync(twice));
        Assert.Equal(1, CountOccurrences(await SingleDocumentTextAsync(once), "typeof(App.Query)"));
    }

    /// <summary>
    ///     Verifies that with several contexts to choose from, the one sharing the type's own namespace is
    ///     offered alone — a feature application keeps its own context, and guessing the wrong one silently
    ///     roots the type where nothing serializes it.
    /// </summary>
    [Fact]
    public async Task PrefersTheContextDeclaredInTheTypesOwnNamespace()
    {
        var source = UnrootedRequestInBlockNamespace + """

                                                      namespace App
                                                      {
                                                          [PortiaJsonContext]
                                                          internal sealed partial class AppJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
                                                      }
                                                      namespace Other
                                                      {
                                                          [PortiaJsonContext]
                                                          internal sealed partial class OtherJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
                                                      }
                                                      """;

        var (actions, _) = await FixAsync(source, "App.Query");

        var action = Assert.Single(actions);
        Assert.Equal("Add global::App.Query to AppJsonContext", action.Title);
    }

    /// <summary>
    ///     Verifies that when no context matches the type's namespace, every candidate is offered rather
    ///     than one picked arbitrarily, and each title carries the namespace that tells them apart.
    /// </summary>
    [Fact]
    public async Task OffersEveryContextWhenNoneMatchesTheTypesNamespace()
    {
        var source = UnrootedRequestInBlockNamespace + """

                                                      namespace First
                                                      {
                                                          [PortiaJsonContext]
                                                          internal sealed partial class FirstJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
                                                      }
                                                      namespace Second
                                                      {
                                                          [PortiaJsonContext]
                                                          internal sealed partial class SecondJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
                                                      }
                                                      """;

        var (actions, _) = await FixAsync(source, "App.Query");

        Assert.Equal(2, actions.Count);
        Assert.Contains(actions, item => item.Title == "Add global::App.Query to FirstJsonContext (First)");
        Assert.Contains(actions, item => item.Title == "Add global::App.Query to SecondJsonContext (Second)");
    }

    /// <summary>
    ///     Verifies that a PORTIA025 diagnostic carrying no type name registers no fix at all, rather than
    ///     offering an action that would write <c>typeof()</c> with nothing in it.
    /// </summary>
    [Fact]
    public async Task RegistersNoFixForADiagnosticWithoutATypeName()
    {
        using var workspace = new AdhocWorkspace();
        var document = AddProject(workspace, UnrootedRequest);
        var descriptor = new DiagnosticDescriptor("PORTIA025", "Missing Portia JSON metadata", "{0}", "Usage",
            DiagnosticSeverity.Warning, true);
        var diagnostic = Diagnostic.Create(descriptor,
            Location.Create((await document.GetSyntaxTreeAsync())!, TextSpan.FromBounds(0, 1)), "no properties");
        var actions = new List<CodeAction>();

        await new JsonMetadataCodeFixProvider().RegisterCodeFixesAsync(
            new CodeFixContext(document, diagnostic, (action, _) => actions.Add(action), CancellationToken.None));

        Assert.Empty(actions);
    }

    /// <summary>
    ///     Verifies the fix advertises exactly the diagnostic it handles and supports fix-all, so a project
    ///     missing a dozen roots is one operation rather than a dozen.
    /// </summary>
    [Fact]
    public void AdvertisesPortia025AndSupportsFixAll()
    {
        var provider = new JsonMetadataCodeFixProvider();

        Assert.Equal("PORTIA025", Assert.Single(provider.FixableDiagnosticIds));
        Assert.NotNull(provider.GetFixAllProvider());
    }

    [Fact]
    public async Task FixAllCreatesOneContextContainingEveryMissingRoot()
    {
        using var workspace = new AdhocWorkspace();
        var document = AddProject(workspace, UnrootedRequest);
        var diagnostics = GeneratorCompilation.Diagnostics(UnrootedRequest, new JsonMetadataDiagnosticGenerator())
            .Where(item => item.Id == "PORTIA025").ToArray();
        Assert.Equal(2, diagnostics.Length);
        var provider = new JsonMetadataCodeFixProvider();
        var context = new FixAllContext(document, provider, FixAllScope.Project, "Portia.CreateJsonContext",
            provider.FixableDiagnosticIds, new TestDiagnosticProvider(diagnostics), CancellationToken.None);

        var action = Assert.IsAssignableFrom<CodeAction>(
            await provider.GetFixAllProvider()!.GetFixAsync(context));
        var changed = await ApplyAsync(document.Project.Solution, action);

        var generated = Assert.Single(changed.Projects.Single().Documents,
            item => item.Name == "PortiaJsonContext.cs");
        var text = (await generated.GetTextAsync()).ToString();
        Assert.Equal(1, CountOccurrences(text, "typeof(App.Query)"));
        Assert.Equal(1, CountOccurrences(text, "typeof(App.Answer)"));
    }

    [Fact]
    public async Task FixAllAddsEveryMissingRootToTheChosenContextOnce()
    {
        var source = UnrootedRequest + """

                                       [PortiaJsonContext]
                                       internal sealed partial class AppJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
                                       """;
        using var workspace = new AdhocWorkspace();
        var document = AddProject(workspace, source);
        var diagnostics = GeneratorCompilation.Diagnostics(source, new JsonMetadataDiagnosticGenerator())
            .Where(item => item.Id == "PORTIA025").ToArray();
        Assert.Equal(2, diagnostics.Length);
        var provider = new JsonMetadataCodeFixProvider();
        var actions = new List<CodeAction>();
        await provider.RegisterCodeFixesAsync(new CodeFixContext(document, diagnostics[0],
            (action, _) => actions.Add(action), CancellationToken.None));
        var equivalenceKey = Assert.Single(actions).EquivalenceKey!;
        var context = new FixAllContext(document, provider, FixAllScope.Project, equivalenceKey,
            provider.FixableDiagnosticIds, new TestDiagnosticProvider(diagnostics), CancellationToken.None);

        var action = Assert.IsAssignableFrom<CodeAction>(
            await provider.GetFixAllProvider()!.GetFixAsync(context));
        var changed = await ApplyAsync(document.Project.Solution, action);
        var text = await SingleDocumentTextAsync(changed);

        Assert.Equal(1, CountOccurrences(text, "typeof(App.Query)"));
        Assert.Equal(1, CountOccurrences(text, "typeof(App.Answer)"));
    }

    static async Task<(IReadOnlyList<CodeAction> Actions, Solution Solution)> FixAsync(string source, string typeName,
        bool expectDiagnostic = true, string projectName = "JsonMetadataFix")
    {
        using var workspace = new AdhocWorkspace();
        var document = AddProject(workspace, source, projectName);
        var candidates = GeneratorCompilation.Diagnostics(source, new JsonMetadataDiagnosticGenerator())
            .Where(item => item.Id == "PORTIA025"
                           && item.Properties.TryGetValue("TypeName", out var name)
                           && name?.Replace("global::", string.Empty) == typeName)
            .ToArray();
        if (!expectDiagnostic && candidates.Length == 0)
        {
            return ([], document.Project.Solution);
        }

        var diagnostic = Assert.Single(candidates);
        var actions = new List<CodeAction>();

        await new JsonMetadataCodeFixProvider().RegisterCodeFixesAsync(
            new CodeFixContext(document, diagnostic, (action, _) => actions.Add(action), CancellationToken.None));

        return (actions, document.Project.Solution);
    }

    static async Task<Solution> ApplyAsync(Solution solution, CodeAction action)
    {
        _ = solution;
        var operations = await action.GetOperationsAsync(CancellationToken.None);
        return Assert.Single(operations.OfType<ApplyChangesOperation>()).ChangedSolution;
    }

    static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal);
             index >= 0;
             index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    static async Task<string> SingleDocumentTextAsync(Solution solution)
    {
        var document = Assert.Single(solution.Projects.Single().Documents, item => item.Name == "Scenario.cs");
        return (await document.GetTextAsync()).ToString();
    }

    // The fix resolves [PortiaJsonContext] through a semantic model, so the project has to carry real
    // references; a detached Project returned by the With* builders never reaches the workspace.
    static Document AddProject(AdhocWorkspace workspace, string source, string projectName = "JsonMetadataFix")
    {
        var projectId = ProjectId.CreateNewId();
        var documentId = DocumentId.CreateNewId(projectId);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator).Append(typeof(Aggregate).Assembly.Location)
            .Distinct(StringComparer.Ordinal).Select(path => MetadataReference.CreateFromFile(path));
        var solution = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(projectId, VersionStamp.Default, projectName, projectName,
                    LanguageNames.CSharp)
                .WithParseOptions(new CSharpParseOptions(LanguageVersion.Preview))
                .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
                .WithMetadataReferences(references))
            .AddDocument(documentId, "Scenario.cs", SourceText.From(source));
        return solution.GetDocument(documentId)!;
    }

    sealed class TestDiagnosticProvider(IReadOnlyCollection<Diagnostic> diagnostics)
        : FixAllContext.DiagnosticProvider
    {
        public override Task<IEnumerable<Diagnostic>> GetDocumentDiagnosticsAsync(Document document,
            CancellationToken cancellationToken) => Task.FromResult(diagnostics.AsEnumerable());

        public override Task<IEnumerable<Diagnostic>> GetProjectDiagnosticsAsync(Project project,
            CancellationToken cancellationToken) => Task.FromResult(Enumerable.Empty<Diagnostic>());

        public override Task<IEnumerable<Diagnostic>> GetAllDiagnosticsAsync(Project project,
            CancellationToken cancellationToken) => Task.FromResult(diagnostics.AsEnumerable());
    }
}
