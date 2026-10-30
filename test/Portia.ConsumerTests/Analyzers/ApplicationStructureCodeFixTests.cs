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
    [InlineData("public class Outer { public class Nested; }")]
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
