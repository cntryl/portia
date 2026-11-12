using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Cntryl.Portia.Consumer;

/// <summary>Namespace repair must retain nested containing types in qualified constructor references.</summary>
public sealed class NestedNamespaceConstructorTests
{
    [Theory]
    [InlineData("ordinary")]
    [InlineData("nested")]
    [InlineData("global_nested")]
    [InlineData("nested_generic")]
    public async Task ShouldPreserveConstructorQualificationGivenNestedMovedType(string form)
    {
        // Arrange
        using var workspace = new AdhocWorkspace();
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(Aggregate).Assembly.Location).Distinct(StringComparer.Ordinal)
            .Select(path => MetadataReference.CreateFromFile(path));
        var project = workspace.AddProject(ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Create(),
                "Shop.Core", "Shop.Core", LanguageNames.CSharp).WithDefaultNamespace("Shop.Core"))
            .WithParseOptions(new CSharpParseOptions(LanguageVersion.Preview))
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddMetadataReferences(references);
        var source = form switch
        {
            "nested_generic" => "namespace Old; public class Price<T> { public class Key<TItem> { } public class Tag { } }",
            "nested" or "global_nested" => "namespace Old; public class Price { public class Key { } }",
            _ => "namespace Old; public class Price { }"
        };
        var document = project.AddDocument("Price.cs", SourceText.From(source),
            ["Features", "Orders", "Values"], "/workspace/Shop.Core/Features/Orders/Values/Price.cs");
        var qualifiedType = form switch
        {
            "nested_generic" => "Old.Price<int>.Key<Old.Price<int>.Tag>",
            "nested" => "Old.Price.Key",
            "global_nested" => "global::Old.Price.Key",
            _ => "Old.Price"
        };

        // Act
        var dependent = document.Project.AddDocument("Use.cs",
            "namespace Client; public class Use { public " + qualifiedType + " Make() => new " + qualifiedType + "(); }");

        // Assert
        Assert.True(workspace.TryApplyChanges(dependent.Project.Solution));
        document = workspace.CurrentSolution.GetDocument(document.Id)!;
        var before = (await document.Project.GetCompilationAsync())!;
        Assert.DoesNotContain(before.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var diagnostics = await before.WithAnalyzers([new ApplicationStructureAnalyzer()],
            ApplicationAnalyzerCompilation.Options("Shop.Core", "/workspace/Shop.Core")).GetAnalyzerDiagnosticsAsync();
        var tree = await document.GetSyntaxTreeAsync();
        var diagnostic = Assert.Single(diagnostics, item => item.Id == "PORTIA202" && item.Location.SourceTree == tree);
        var actions = new List<CodeAction>();
        await new ApplicationStructureCodeFixProvider().RegisterCodeFixesAsync(new CodeFixContext(document, diagnostic,
            (action, _) => actions.Add(action), CancellationToken.None));

        var operations = await Assert.Single(actions).GetOperationsAsync(CancellationToken.None);
        var changed = Assert.Single(operations.OfType<ApplyChangesOperation>()).ChangedSolution;
        var changedProject = changed.GetProject(document.Project.Id)!;
        var after = (await changedProject.GetCompilationAsync())!;
        var errors = after.GetDiagnostics().Where(item => item.Severity == DiagnosticSeverity.Error).ToArray();
        var changedUse = (await changed.GetDocument(dependent.Id)!.GetTextAsync()).ToString();

        Assert.True(errors.Length == 0, string.Join(Environment.NewLine, errors.Select(item => item.ToString()))
            + Environment.NewLine + changedUse);
    }
}
