using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Cntryl.Portia;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

static class NamespaceCodeActionCampaign
{
    const string ExpectedNamespace = "Shop.Core.Features.Orders.Values";
#pragma warning disable RS2008 // This descriptor is a benchmark fixture, not a shipped analyzer rule.
    static readonly DiagnosticDescriptor Descriptor = new("PORTIA202", "Namespace does not follow folders", "Type namespace is incorrect",
        "PortiaStructure", DiagnosticSeverity.Warning, true);
#pragma warning restore RS2008
    static readonly MetadataReference[] PlatformReferences = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator).Distinct(StringComparer.Ordinal).Select(path => MetadataReference.CreateFromFile(path)).ToArray();

    public static async Task RunAsync(string output, int repetitions, int referenceCount)
    {
        if (repetitions < 2 || referenceCount < 1)
            throw new ArgumentOutOfRangeException(nameof(repetitions), "Use at least two repetitions and one reference.");

        Directory.CreateDirectory(output);
        var rows = new List<Row>();
        for (var repetition = -1; repetition < repetitions; repetition++)
        {
            using var workspace = new AdhocWorkspace();
            var (document, diagnostic) = CreateFixture(workspace, referenceCount);
            var provider = new ApplicationStructureCodeFixProvider();
            var actions = new List<CodeAction>();

            var discoveryStarted = Stopwatch.GetTimestamp();
            await provider.RegisterCodeFixesAsync(new CodeFixContext(document, diagnostic,
                (action, _) => actions.Add(action), CancellationToken.None)).ConfigureAwait(false);
            var discoveryMilliseconds = Stopwatch.GetElapsedTime(discoveryStarted).TotalMilliseconds;
            var action = actions.Single();

            var previewStarted = Stopwatch.GetTimestamp();
            var operations = await action.GetOperationsAsync(CancellationToken.None).ConfigureAwait(false);
            var previewMilliseconds = Stopwatch.GetElapsedTime(previewStarted).TotalMilliseconds;
            var changedSolution = operations.OfType<ApplyChangesOperation>().Single().ChangedSolution;

            var applicationStarted = Stopwatch.GetTimestamp();
            if (!workspace.TryApplyChanges(changedSolution))
                throw new InvalidOperationException("The namespace refactoring could not be applied to its workspace.");
            var applicationMilliseconds = Stopwatch.GetElapsedTime(applicationStarted).TotalMilliseconds;

            var changedDocument = workspace.CurrentSolution.GetDocument(document.Id)!;
            var changedText = (await changedDocument.GetTextAsync().ConfigureAwait(false)).ToString();
            if (!changedText.Contains("namespace " + ExpectedNamespace, StringComparison.Ordinal))
                throw new InvalidOperationException("The namespace code action did not update the source declaration.");
            var consumer = workspace.CurrentSolution.Projects.Single(project => project.Name == "Shop.Api");
            var useText = string.Join("\n", consumer.Documents
                .Select(item => item.GetTextAsync().Result.ToString()));
            if (useText.Contains("Old.Price", StringComparison.Ordinal))
                throw new InvalidOperationException("The namespace code action left a qualified consumer reference unchanged.");

            if (repetition >= 0)
            {
                rows.Add(new Row(repetition, discoveryMilliseconds, previewMilliseconds, applicationMilliseconds, referenceCount));
                await File.WriteAllTextAsync(Path.Combine(output, "raw.json"), JsonSerializer.Serialize(rows)).ConfigureAwait(false);
                Console.WriteLine($"namespace action: saved repetition {repetition + 1}/{repetitions}.");
            }
        }

        var summary = new
        {
            repetitions,
            references = referenceCount,
            discoveryP50Milliseconds = Percentile(rows.Select(row => row.DiscoveryMilliseconds), 0.50),
            previewP50Milliseconds = Percentile(rows.Select(row => row.PreviewMilliseconds), 0.50),
            applicationP50Milliseconds = Percentile(rows.Select(row => row.ApplicationMilliseconds), 0.50),
            discoveryP95Milliseconds = Percentile(rows.Select(row => row.DiscoveryMilliseconds), 0.95),
            previewP95Milliseconds = Percentile(rows.Select(row => row.PreviewMilliseconds), 0.95),
            applicationP95Milliseconds = Percentile(rows.Select(row => row.ApplicationMilliseconds), 0.95)
        };
        await File.WriteAllTextAsync(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(summary)).ConfigureAwait(false);
    }

    static (Document Document, Diagnostic Diagnostic) CreateFixture(AdhocWorkspace workspace, int referenceCount)
    {
        var core = workspace.AddProject(ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Create(),
                "Shop.Core", "Shop.Core", LanguageNames.CSharp).WithDefaultNamespace("Shop.Core"))
            .WithParseOptions(new CSharpParseOptions(LanguageVersion.Preview))
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddMetadataReferences(PlatformReferences);
        var document = core.AddDocument("Price.cs", SourceText.From("namespace Old; public sealed record Price(string Name);"),
            ["Features", "Orders", "Values"], "/workspace/Shop.Core/Features/Orders/Values/Price.cs");
        var apiId = ProjectId.CreateNewId();
        var solution = document.Project.Solution.AddProject(ProjectInfo.Create(apiId, VersionStamp.Create(),
                "Shop.Api", "Shop.Api", LanguageNames.CSharp).WithDefaultNamespace("Shop.Api"))
            .WithProjectParseOptions(apiId, new CSharpParseOptions(LanguageVersion.Preview))
            .WithProjectCompilationOptions(apiId, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddMetadataReferences(apiId, PlatformReferences)
            .AddProjectReference(apiId, new ProjectReference(document.Project.Id));

        var usage = new StringBuilder("namespace Shop.Api; public static class Use {\n");
        for (var index = 0; index < referenceCount; index++)
            usage.Append(CultureInfo.InvariantCulture, $"public static Old.Price Create{index}() => new Old.Price(\"{index}\");\n");
        usage.AppendLine("}");
        var apiDocument = solution.GetProject(apiId)!.AddDocument("Use.cs", SourceText.From(usage.ToString()), ["Routes"],
            "/workspace/Shop.Api/Routes/Use.cs");
        solution = apiDocument.Project.Solution;
        if (!workspace.TryApplyChanges(solution))
            throw new InvalidOperationException("The namespace benchmark fixture could not be loaded.");
        document = workspace.CurrentSolution.GetDocument(document.Id)!;
        var root = document.GetSyntaxRootAsync().Result!;
        var declaration = root.DescendantNodes().OfType<RecordDeclarationSyntax>().Single();
        var properties = ImmutableDictionary<string, string?>.Empty
            .Add("ExpectedNamespace", ExpectedNamespace)
            .Add("Folders", "Features/Orders/Values");
        return (document, Diagnostic.Create(Descriptor, declaration.Identifier.GetLocation(), properties));
    }

    static double Percentile(IEnumerable<double> values, double fraction)
    {
        var sorted = values.Order().ToArray();
        var rank = Math.Clamp((int)Math.Ceiling(sorted.Length * fraction), 1, sorted.Length);
        return sorted[rank - 1];
    }

    sealed record Row(int Repetition, double DiscoveryMilliseconds, double PreviewMilliseconds,
        double ApplicationMilliseconds, int References);
}
