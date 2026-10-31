using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Cntryl.Portia.Consumer;

public sealed class ApplicationStructureCodeFixTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ShouldRegenerateConstructorReferencesGivenNamespaceFix(bool fixAll, bool blockNamespace)
    {
        using var workspace = new AdhocWorkspace();
        var source = blockNamespace ? "namespace Old { public sealed record Price(string Value); }"
            : "namespace Old; public sealed record Price(string Value);";
        var document = AddDocument(workspace, "Shop.Core", "Features/Orders/Values/Price.cs", source);
        Assert.True(workspace.TryApplyChanges(document.Project.AddAnalyzerReference(new GeneratedConstructorReference()).Solution));
        document = workspace.CurrentSolution.GetDocument(document.Id)!;
        Assert.Single(await document.Project.GetSourceGeneratedDocumentsAsync());
        CodeAction action;
        if (fixAll)
        {
            var provider = new ApplicationStructureCodeFixProvider();
            var context = new FixAllContext(document, provider, FixAllScope.Solution, "Portia.SyncApplicationNamespace",
                ["PORTIA202"], new StructureDiagnosticProvider(), CancellationToken.None);
            action = (await provider.GetFixAllProvider()!.GetFixAsync(context))!;
            Assert.NotNull(action);
        }
        else
        {
            var (actions, _) = await ActionsAsync(document, "PORTIA202");
            action = Assert.Single(actions);
        }
        var solution = await ApplyAsync(action);
        var project = solution.GetProject(document.Project.Id)!;
        await AssertCompilesAsync(project);
        Assert.Contains("namespace Shop.Core.Features.Orders.Values", (await solution.GetDocument(document.Id)!.GetTextAsync()).ToString(), StringComparison.Ordinal);
        var generated = Assert.Single(await project.GetSourceGeneratedDocumentsAsync());
        Assert.Contains("new global::Shop.Core.Features.Orders.Values.Price", (await generated.GetTextAsync()).ToString(), StringComparison.Ordinal);
    }

    sealed class GeneratedConstructorReference : AnalyzerReference
    {
        readonly ImmutableArray<ISourceGenerator> _generators = [new ConstructorGenerator().AsSourceGenerator()];
        public override string? FullPath => null;
        public override object Id => this;
        public override ImmutableArray<DiagnosticAnalyzer> GetAnalyzers(string language) => [];
        public override ImmutableArray<DiagnosticAnalyzer> GetAnalyzersForAllLanguages() => [];
        public override ImmutableArray<ISourceGenerator> GetGenerators(string language) => _generators;
    }

    sealed class ConstructorGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var type = context.CompilationProvider.Select(static (compilation, ct) => compilation
                .GetSymbolsWithName("Price", SymbolFilter.Type, ct).OfType<INamedTypeSymbol>().Single()
                .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
            context.RegisterSourceOutput(type, static (output, name) => output.AddSource("GeneratedUse.g.cs",
                "internal static class GeneratedUse { public static object Create() => new " + name + "(\"generated\"); }"));
        }
    }

    [Fact]
    public async Task ShouldAllowMatchingDestinationNameInAnotherProject()
    {
        using var workspace = new AdhocWorkspace();
        var document = AddDocument(workspace, "Shop.Core", "Features/Orders/Values/Wrong.cs",
            "namespace Shop.Core.Features.Orders.Values; public record Price;");
        _ = AddDocument(workspace, "Other.Core", "Features/Orders/Values/Price.cs",
            "namespace Other.Core.Features.Orders.Values; public record Price;");
        document = workspace.CurrentSolution.GetDocument(document.Id)!;
        var diagnostics = await DiagnosticsAsync(document, new ApplicationStructureAnalyzer());
        var actions = new List<CodeAction>();
        await new ApplicationStructureCodeFixProvider().RegisterCodeFixesAsync(new CodeFixContext(document,
            diagnostics.Single(item => item.Id == "PORTIA201"), (action, _) => actions.Add(action), CancellationToken.None));
        var solution = await ApplyAsync(Assert.Single(actions));
        Assert.Equal("Price.cs", solution.GetDocument(document.Id)!.Name);
    }

    [Theory]
    [InlineData(FixAllScope.Document)]
    [InlineData(FixAllScope.Project)]
    [InlineData(FixAllScope.Solution)]
    public async Task ShouldSplitEveryTopLevelTypeGivenFixAll(FixAllScope scope)
    {
        using var workspace = new AdhocWorkspace();
        const string source = "namespace Shop.Core.Features.Orders.Values; public record Price { private class Fixture; } public record Currency; public record Tax;";
        var document = AddDocument(workspace, "Shop.Core", "Features/Orders/Values/Price.cs", source);
        var provider = new ApplicationStructureCodeFixProvider();
        var context = new FixAllContext(document, provider, scope, "Portia.MoveApplicationType", ["PORTIA200"],
            new StructureDiagnosticProvider(), CancellationToken.None);
        var action = await provider.GetFixAllProvider()!.GetFixAsync(context);
        Assert.NotNull(action);
        var solution = await ApplyAsync(action);
        var project = solution.GetProject(document.Project.Id)!;
        Assert.Equal(["Currency.cs", "Price.cs", "Tax.cs"], project.Documents.Select(item => item.Name).OrderBy(name => name));
        Assert.Contains("class Fixture", (await solution.GetDocument(document.Id)!.GetTextAsync()).ToString(), StringComparison.Ordinal);
        await AssertCompilesAsync(project);
        foreach (var item in project.Documents)
            Assert.DoesNotContain(await DiagnosticsAsync(item, new ApplicationStructureAnalyzer()), item => item.Id == "PORTIA200");
    }

    [Theory]
    [InlineData(FixAllScope.Document)]
    [InlineData(FixAllScope.Project)]
    [InlineData(FixAllScope.Solution)]
    public async Task ShouldRenameEveryMisnamedFileGivenFixAll(FixAllScope scope)
    {
        using var workspace = new AdhocWorkspace();
        var first = AddDocument(workspace, "Shop.Core", "Features/Orders/Values/Page.cs",
            "namespace Shop.Core.Features.Orders.Values; public record Page<T>(T Value);");
        var second = first.Project.AddDocument("Pair.cs",
            "namespace Shop.Core.Features.Orders.Values; public record Pair<T, U>(T First, U Second);",
            first.Folders, "/workspace/Shop.Core/Features/Orders/Values/Pair.cs");
        Assert.True(workspace.TryApplyChanges(second.Project.Solution));
        first = workspace.CurrentSolution.GetDocument(first.Id)!;
        var provider = new ApplicationStructureCodeFixProvider();
        var context = new FixAllContext(first, provider, scope, "Portia.RenameApplicationFile", ["PORTIA201"],
            new StructureDiagnosticProvider(), CancellationToken.None);

        var action = await provider.GetFixAllProvider()!.GetFixAsync(context);
        Assert.NotNull(action);
        var solution = await ApplyAsync(action);

        var project = solution.GetProject(first.Project.Id)!;
        string[] expectedNames = scope == FixAllScope.Document ? ["PageOfT.cs", "Pair.cs"]
            : ["PageOfT.cs", "PairOfT2.cs"];
        Assert.Equal(expectedNames, project.Documents.Select(document => document.Name)
            .OrderBy(name => name, StringComparer.Ordinal));
        var remaining = scope == FixAllScope.Document ? "Pair.cs" : null;
        foreach (var document in project.Documents)
        {
            var diagnostics = await DiagnosticsAsync(document, new ApplicationStructureAnalyzer());
            if (document.Name == remaining)
                Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "PORTIA201");
            else
                Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "PORTIA201");
        }
        await AssertCompilesAsync(project);
    }

    [Theory]
    [InlineData(FixAllScope.Document)]
    [InlineData(FixAllScope.Project)]
    [InlineData(FixAllScope.Solution)]
    public async Task ShouldSyncNamespacesGivenFixAll(FixAllScope scope)
    {
        using var workspace = new AdhocWorkspace();
        var first = AddDocument(workspace, "Shop.Core", "Features/Orders/Values/Price.cs", "namespace Old; public record Price;");
        var second = first.Project.AddDocument("Currency.cs", "namespace Old; public record Currency;", first.Folders,
            "/workspace/Shop.Core/Features/Orders/Values/Currency.cs");
        Assert.True(workspace.TryApplyChanges(second.Project.Solution));
        first = workspace.CurrentSolution.GetDocument(first.Id)!;
        var provider = new ApplicationStructureCodeFixProvider();
        var context = new FixAllContext(first, provider, scope, "Portia.SyncApplicationNamespace", ["PORTIA202"],
            new StructureDiagnosticProvider(), CancellationToken.None);

        var action = await provider.GetFixAllProvider()!.GetFixAsync(context);
        Assert.NotNull(action);
        var solution = await ApplyAsync(action);

        var project = solution.GetProject(first.Project.Id)!;
        foreach (var document in project.Documents)
        {
            var text = (await document.GetTextAsync()).ToString();
            var diagnostics = await DiagnosticsAsync(document, new ApplicationStructureAnalyzer());
            if (scope == FixAllScope.Document && document.Name == "Currency.cs")
            {
                Assert.Contains("namespace Old", text, StringComparison.Ordinal);
                Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "PORTIA202");
            }
            else
            {
                Assert.Contains("namespace Shop.Core.Features.Orders.Values", text, StringComparison.Ordinal);
                Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "PORTIA202");
            }
        }
        await AssertCompilesAsync(project);
    }

    sealed class StructureDiagnosticProvider : FixAllContext.DiagnosticProvider
    {
        public override async Task<IEnumerable<Diagnostic>> GetDocumentDiagnosticsAsync(Document document, CancellationToken cancellationToken) =>
            await DiagnosticsAsync(document, new ApplicationStructureAnalyzer());

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

    [Theory]
    [InlineData("namespace Shop.Core.Features.Orders.Values;\n")]
    [InlineData("namespace Shop.Core.Features.Orders.Values\n{\n")]
    public async Task ShouldMoveTypeAndPreserveReferencesGivenSiblingTypes(string namespaceHeader)
    {
        // Arrange
        using var workspace = new AdhocWorkspace();
        var source = "using System;\n" + namespaceHeader + """
            public record Price(Currency Currency);
            /// <summary>Business currency.</summary>
            [Obsolete("Kept for this test")]
            public record Currency(decimal Value);
            """ + (namespaceHeader.EndsWith("{\n", StringComparison.Ordinal) ? "\n}" : "");
        var document = AddDocument(workspace, "Shop.Core", "Features/Orders/Values/Price.cs", source);

        // Act
        var (actions, _) = await ActionsAsync(document, "PORTIA200");
        var action = Assert.Single(actions);
        var solution = await ApplyAsync(action);

        // Assert
        Assert.Equal("Move 'Currency' to 'Currency.cs'", action.Title);
        var documents = solution.GetProject(document.Project.Id)!.Documents.ToArray();
        Assert.Equal(2, documents.Length);
        var moved = Assert.Single(documents, item => item.Name == "Currency.cs");
        var movedText = (await moved.GetTextAsync()).ToString();
        Assert.Contains("Business currency.", movedText, StringComparison.Ordinal);
        Assert.Contains("[Obsolete", movedText, StringComparison.Ordinal);
        Assert.Contains("using System;", movedText, StringComparison.Ordinal);
        Assert.EndsWith("/Features/Orders/Values/Currency.cs", moved.FilePath, StringComparison.Ordinal);
        Assert.DoesNotContain("record Currency", (await solution.GetDocument(document.Id)!.GetTextAsync()).ToString(), StringComparison.Ordinal);
        await AssertCompilesAsync(solution.GetProject(document.Project.Id)!);
        foreach (var item in documents)
            Assert.Empty(await DiagnosticsAsync(item, new ApplicationStructureAnalyzer()));
    }

    [Fact]
    public async Task ShouldRenameFileWithoutRenamingTypeGivenGenericDeclaration()
    {
        // Arrange
        using var workspace = new AdhocWorkspace();
        const string source = "namespace Shop.Core.Features.Orders.Values; public record Page<T>(T Value);";
        var document = AddDocument(workspace, "Shop.Core", "Features/Orders/Values/Page.cs", source);

        // Act
        var (actions, _) = await ActionsAsync(document, "PORTIA201");
        var action = Assert.Single(actions);
        var solution = await ApplyAsync(action);

        // Assert
        Assert.Equal("Rename file to 'PageOfT.cs'", action.Title);
        var changed = solution.GetDocument(document.Id)!;
        Assert.Equal("PageOfT.cs", changed.Name);
        Assert.EndsWith("/PageOfT.cs", changed.FilePath, StringComparison.Ordinal);
        Assert.Equal(source, (await changed.GetTextAsync()).ToString());
        Assert.Empty(await DiagnosticsAsync(changed, new ApplicationStructureAnalyzer()));
        await AssertCompilesAsync(changed.Project);
    }

    [Fact]
    public async Task ShouldKeepMatchingTypeInPlaceGivenDifferentDeclarationOrder()
    {
        // Arrange
        using var workspace = new AdhocWorkspace();
        const string source = "namespace Shop.Core.Features.Orders.Values; public record Price(Currency Currency); public record Currency;";
        var document = AddDocument(workspace, "Shop.Core", "Features/Orders/Values/Currency.cs", source);

        // Act
        var (actions, _) = await ActionsAsync(document, "PORTIA200");
        var action = Assert.Single(actions);
        var solution = await ApplyAsync(action);

        // Assert
        Assert.Equal("Move 'Price' to 'Price.cs'", action.Title);
        Assert.Equal(2, solution.GetProject(document.Project.Id)!.DocumentIds.Count);
        Assert.Contains("record Currency", (await solution.GetDocument(document.Id)!.GetTextAsync()).ToString(), StringComparison.Ordinal);
        Assert.Empty(await DiagnosticsAsync(solution.GetDocument(document.Id)!, new ApplicationStructureAnalyzer()));
        await AssertCompilesAsync(solution.GetProject(document.Project.Id)!);
    }

    [Fact]
    public async Task ShouldUpdateDependentProjectGivenNamespaceFix()
    {
        // Arrange
        using var workspace = new AdhocWorkspace();
        var document = AddDocument(workspace, "Shop.Core", "Features/Orders/Values/Price.cs", "namespace Old; public record Price;");
        var consumer = workspace.AddProject("Shop.Api", LanguageNames.CSharp)
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddMetadataReferences(document.Project.MetadataReferences)
            .AddProjectReference(new ProjectReference(document.Project.Id))
            .AddDocument("Use.cs", "public class Use { public Old.Price Make() => new Old.Price(); }");
        Assert.True(workspace.TryApplyChanges(consumer.Project.Solution));
        document = workspace.CurrentSolution.GetDocument(document.Id)!;

        // Act
        var (actions, _) = await ActionsAsync(document, "PORTIA202");
        var solution = await ApplyAsync(Assert.Single(actions));

        // Assert
        await AssertCompilesAsync(solution.GetProject(document.Project.Id)!);
        await AssertCompilesAsync(solution.GetProject(consumer.Project.Id)!);
        Assert.DoesNotContain("Old.Price", (await solution.GetDocument(consumer.Id)!.GetTextAsync()).ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShouldUpdateOnlyMovedTypeReferencesGivenSharedOldNamespace()
    {
        // Arrange
        using var workspace = new AdhocWorkspace();
        var document = AddDocument(workspace, "Shop.Core", "Features/Orders/Values/Price.cs", "namespace Old; public record Price;");
        var project = document.Project
            .AddDocument("Other.cs", "namespace Old; public record Other;").Project
            .AddDocument("Use.cs", "namespace Client; public class Use { public Old.Price Make() => new Old.Price(); public Old.Other Other() => new Old.Other(); }").Project;
        Assert.True(workspace.TryApplyChanges(project.Solution));
        document = workspace.CurrentSolution.GetDocument(document.Id)!;
        Assert.Equal("Shop.Core", document.Project.DefaultNamespace);

        // Act
        var (actions, _) = await ActionsAsync(document, "PORTIA202");
        var action = Assert.Single(actions);
        var solution = await ApplyAsync(action);

        // Assert
        Assert.Equal("Change namespace to 'Shop.Core.Features.Orders.Values' and update references", action.Title);
        var changed = solution.GetDocument(document.Id)!;
        Assert.Contains("namespace Shop.Core.Features.Orders.Values", (await changed.GetTextAsync()).ToString(), StringComparison.Ordinal);
        var other = Assert.Single(changed.Project.Documents, item => item.Name == "Other.cs");
        Assert.Contains("namespace Old", (await other.GetTextAsync()).ToString(), StringComparison.Ordinal);
        Assert.Equal(document.FilePath, changed.FilePath);
        Assert.Equal(document.Folders, changed.Folders);
        Assert.Empty(await DiagnosticsAsync(changed, new ApplicationStructureAnalyzer()));
        await AssertCompilesAsync(changed.Project);
    }

    [Theory]
    [InlineData("\"orders.place_order\"")]
    [InlineData("name: \"orders.place_order\"")]
    public async Task ShouldPreserveWireIdentityGivenImplicitSchemaVersion(string argument)
    {
        // Arrange
        using var workspace = new AdhocWorkspace();
        var source = $"using Cntryl.Portia; namespace Shop.Common.Features.Orders.Commands; [Discriminator({argument})] public record PlaceOrder : IRequest;";
        var document = AddDocument(workspace, "Shop.Common", "Features/Orders/Commands/PlaceOrder.cs", source);

        // Act
        var (actions, _) = await ActionsAsync(document, "PORTIA210");
        var action = Assert.Single(actions);
        var solution = await ApplyAsync(action);

        // Assert
        Assert.Equal("Make schema version 1 explicit", action.Title);
        var changed = solution.GetDocument(document.Id)!;
        var text = (await changed.GetTextAsync()).ToString();
        Assert.Contains("orders.place_order", text, StringComparison.Ordinal);
        Assert.Contains("version: 1", text, StringComparison.Ordinal);
        Assert.Empty(await DiagnosticsAsync(changed, new ApplicationContractAnalyzer()));
        var compilation = await changed.Project.GetCompilationAsync();
        var symbol = compilation!.GetTypeByMetadataName("Shop.Common.Features.Orders.Commands.PlaceOrder")!;
        var attribute = Assert.Single(symbol.GetAttributes());
        Assert.Equal("orders.place_order", attribute.ConstructorArguments[0].Value);
        Assert.Equal(1, attribute.ConstructorArguments[1].Value);
        await AssertCompilesAsync(changed.Project);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[Discriminator(\"other.place_order\", 1)]")]
    [InlineData("[Discriminator(\"orders.place_order\", 0)]")]
    public async Task ShouldOfferNoIdentityRewriteGivenContractDecision(string attribute)
    {
        // Arrange
        using var workspace = new AdhocWorkspace();
        var source = $"using Cntryl.Portia; namespace Shop.Common.Features.Orders.Commands; {attribute} public record PlaceOrder : IRequest;";
        var document = AddDocument(workspace, "Shop.Common", "Features/Orders/Commands/PlaceOrder.cs", source);

        // Act
        var (actions, _) = await ActionsAsync(document, "PORTIA210");

        // Assert
        Assert.Empty(actions);
    }

    [Theory]
    [InlineData("file class Local; public class Other;")]
    [InlineData("#if true\npublic class First; public class Second;\n#endif")]
    public async Task ShouldDeclineSplitGivenScopeOrDirectiveDecision(string source)
    {
        // Arrange
        using var workspace = new AdhocWorkspace();
        var document = AddDocument(workspace, "Shop.Core", "Features/Orders/Values/Outer.cs", source);

        // Act
        var (actions, _) = await ActionsAsync(document, "PORTIA200");

        // Assert
        Assert.Empty(actions);
    }

    [Theory]
    [InlineData("#nullable enable\n")]
    [InlineData("#pragma warning disable CS1591\n")]
    public async Task ShouldPreserveSimpleFileDirectiveGivenSplit(string directive)
    {
        using var workspace = new AdhocWorkspace();
        var source = directive + "namespace Shop.Core.Features.Orders.Values; public record Price; public record Currency;";
        var document = AddDocument(workspace, "Shop.Core", "Features/Orders/Values/Price.cs", source);

        var (actions, _) = await ActionsAsync(document, "PORTIA200");
        var solution = await ApplyAsync(Assert.Single(actions));

        var moved = Assert.Single(solution.GetProject(document.Project.Id)!.Documents, item => item.Name == "Currency.cs");
        Assert.StartsWith(directive, (await moved.GetTextAsync()).ToString(), StringComparison.Ordinal);
        await AssertCompilesAsync(solution.GetProject(document.Project.Id)!);
    }

    [Theory]
    [InlineData("PORTIA200", "public record Price; public record Currency;", "Price.cs", "Currency.cs")]
    [InlineData("PORTIA201", "public record Currency;", "Wrong.cs", "Currency.cs")]
    public async Task ShouldPreserveExistingFilesGivenDestinationCollision(string id, string source, string filename, string destination)
    {
        // Arrange
        using var workspace = new AdhocWorkspace();
        var document = AddDocument(workspace, "Shop.Core", "Features/Orders/Values/" + filename, source);
        var other = document.Project.AddDocument(destination, "", document.Folders,
            "/workspace/Shop.Core/Features/Orders/Values/" + destination);
        Assert.True(workspace.TryApplyChanges(other.Project.Solution));
        document = workspace.CurrentSolution.GetDocument(document.Id)!;

        // Act
        var (actions, _) = await ActionsAsync(document, id);

        // Assert
        Assert.Empty(actions);
        Assert.Equal(2, workspace.CurrentSolution.GetProject(document.Project.Id)!.DocumentIds.Count);
    }

    [Fact]
    public async Task ShouldDeclineSplitGivenDestinationFileOutsideSolution()
    {
        var directory = Path.Combine(Path.GetTempPath(), "portia-codefix-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "Currency.cs"), "public record Existing;");
            using var workspace = new AdhocWorkspace();
            var project = workspace.AddProject(ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Create(),
                    "Shop.Core", "Shop.Core", LanguageNames.CSharp).WithDefaultNamespace("Shop.Core"))
                .WithParseOptions(new CSharpParseOptions(LanguageVersion.Preview))
                .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
                .AddMetadataReferences(((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                    .Select(file => MetadataReference.CreateFromFile(file)));
            var document = project.AddDocument("Price.cs", SourceText.From(
                "namespace Shop.Core.Features.Orders.Values; public record Price; public record Currency;"),
                ["Features", "Orders", "Values"], Path.Combine(directory, "Price.cs"));
            Assert.True(workspace.TryApplyChanges(document.Project.Solution));

            var (actions, _) = await ActionsAsync(workspace.CurrentSolution.GetDocument(document.Id)!, "PORTIA200");

            Assert.Empty(actions);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ShouldLeaveRenameCycleUnchangedGivenFixAll()
    {
        using var workspace = new AdhocWorkspace();
        var first = AddDocument(workspace, "Shop.Core", "Features/Orders/Values/Bar.cs",
            "namespace Shop.Core.Features.Orders.Values; public record Foo;");
        var second = first.Project.AddDocument("Foo.cs", "namespace Shop.Core.Features.Orders.Values; public record Bar;",
            first.Folders, "/workspace/Shop.Core/Features/Orders/Values/Foo.cs");
        Assert.True(workspace.TryApplyChanges(second.Project.Solution));
        first = workspace.CurrentSolution.GetDocument(first.Id)!;
        var provider = new ApplicationStructureCodeFixProvider();
        var context = new FixAllContext(first, provider, FixAllScope.Project, "Portia.RenameApplicationFile",
            ["PORTIA201"], new StructureDiagnosticProvider(), CancellationToken.None);

        var action = await provider.GetFixAllProvider()!.GetFixAsync(context);
        Assert.NotNull(action);
        var solution = await ApplyAsync(action);

        Assert.Equal(["Bar.cs", "Foo.cs"], solution.GetProject(first.Project.Id)!.Documents
            .Select(document => document.Name).OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal("namespace Shop.Core.Features.Orders.Values; public record Foo;",
            (await solution.GetDocument(first.Id)!.GetTextAsync()).ToString());
        foreach (var document in solution.GetProject(first.Project.Id)!.Documents)
            Assert.Contains(await DiagnosticsAsync(document, new ApplicationStructureAnalyzer()),
                diagnostic => diagnostic.Id == "PORTIA201");
    }

    [Fact]
    public async Task ShouldDeclineNamespaceMoveGivenTypeConflict()
    {
        // Arrange
        using var workspace = new AdhocWorkspace();
        var document = AddDocument(workspace, "Shop.Core", "Features/Orders/Values/Price.cs", "namespace Old; public record Price;");
        var other = document.Project.AddDocument("Existing.cs", "namespace Shop.Core.Features.Orders.Values; public record Price;");
        Assert.True(workspace.TryApplyChanges(other.Project.Solution));
        document = workspace.CurrentSolution.GetDocument(document.Id)!;

        // Act
        var (actions, _) = await ActionsAsync(document, "PORTIA202");

        // Assert
        Assert.Empty(actions);
        await AssertCompilesAsync(document.Project);
    }

    static Document AddDocument(AdhocWorkspace workspace, string projectName, string path, string source)
    {
        var project = workspace.AddProject(ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Create(),
                projectName, projectName, LanguageNames.CSharp).WithDefaultNamespace(projectName))
            .WithParseOptions(new CSharpParseOptions(LanguageVersion.Preview))
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddMetadataReferences(((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Append(typeof(Aggregate).Assembly.Location).Distinct(StringComparer.Ordinal)
                .Select(file => MetadataReference.CreateFromFile(file)));
        var parts = path.Split('/');
        var document = project.AddDocument(parts[^1], SourceText.From(source), parts.Take(parts.Length - 1), "/workspace/" + projectName + "/" + path);
        Assert.True(workspace.TryApplyChanges(document.Project.Solution));
        return workspace.CurrentSolution.GetDocument(document.Id)!;
    }

    static async Task<ImmutableArray<Diagnostic>> DiagnosticsAsync(Document document, DiagnosticAnalyzer analyzer)
    {
        var compilation = await document.Project.GetCompilationAsync();
        Assert.NotNull(compilation);
        Assert.DoesNotContain(compilation.GetDiagnostics(), item => item.Severity == DiagnosticSeverity.Error);
        var diagnostics = await compilation.WithAnalyzers([analyzer], ApplicationAnalyzerCompilation.Options(document.Project.Name,
            "/workspace/" + document.Project.Name)).GetAnalyzerDiagnosticsAsync();
        var tree = await document.GetSyntaxTreeAsync();
        return [.. diagnostics.Where(item => item.Location.SourceTree == tree)];
    }

    static async Task<(IReadOnlyList<CodeAction> Actions, Diagnostic Diagnostic)> ActionsAsync(Document document, string id)
    {
        DiagnosticAnalyzer analyzer = id == "PORTIA210" ? new ApplicationContractAnalyzer() : new ApplicationStructureAnalyzer();
        var diagnostics = await DiagnosticsAsync(document, analyzer);
        var diagnostic = Assert.Single(diagnostics, item => item.Id == id);
        var actions = new List<CodeAction>();
        await new ApplicationStructureCodeFixProvider().RegisterCodeFixesAsync(new CodeFixContext(document, diagnostic,
            (action, _) => actions.Add(action), CancellationToken.None));
        return (actions, diagnostic);
    }

    static async Task<Solution> ApplyAsync(CodeAction action)
    {
        var operations = await action.GetOperationsAsync(CancellationToken.None);
        return Assert.Single(operations.OfType<ApplyChangesOperation>()).ChangedSolution;
    }

    static async Task AssertCompilesAsync(Project project)
    {
        var compilation = await project.GetCompilationAsync();
        Assert.NotNull(compilation);
        var errors = compilation.GetDiagnostics().Where(item => item.Severity == DiagnosticSeverity.Error).ToArray();
        var texts = await Task.WhenAll(project.Documents.Select(async document => (await document.GetTextAsync()).ToString()));
        Assert.True(errors.Length == 0, string.Join(Environment.NewLine, errors.Select(item => item.ToString()).Concat(texts)));
    }
}
