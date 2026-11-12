using System.Text.RegularExpressions;

namespace Cntryl.Portia.Consumer;

public sealed partial class DocumentationContractTests
{
    static readonly string Root = FindRoot();

    static readonly string[] DiagnosticSourceDirectories =
        ["src/Portia.Generators", "src/Portia.AspNetCore.Generators"];

    static readonly string[] DiagnosticReleaseManifests =
    [
        "docs/AnalyzerReleases.Shipped.md",
        "docs/AnalyzerReleases.Unshipped.md",
        "src/Portia.AspNetCore.Generators/AnalyzerReleases.Shipped.md",
        "src/Portia.AspNetCore.Generators/AnalyzerReleases.Unshipped.md"
    ];

    static readonly string[] Maintained =
    [
        "README.md",
        "docs/getting-started.md",
        "docs/application-setup.md",
        "docs/projectors-and-reactors.md",
        "docs/cassie-projections.md",
        "docs/validation-authority.md",
        "docs/request-context.md",
        "docs/observability.md",
        "docs/native-aot.md",
        "docs/scope.md",
        "docs/design-decisions.md"
    ];

    [Fact]
    public void WorkloadContractsExposeLeaseLifetimeWithoutPortiaFencingProtocol()
    {
        // Arrange
        var contextType = typeof(WorkloadContext);

        // Act
        var fencingToken = contextType.GetProperty("FencingToken");
        var exportedTypes = contextType.Assembly.GetExportedTypes();
        var runParameters = typeof(IPartitionWorkload).GetMethod(nameof(IPartitionWorkload.RunAsync))!
            .GetParameters().Select(parameter => parameter.ParameterType);

        // Assert
        Assert.Null(fencingToken);
        Assert.DoesNotContain(exportedTypes, type => type.Name.Contains("Fencing", StringComparison.Ordinal));
        Assert.Equal([typeof(string), typeof(CancellationToken)], runParameters);
    }

    [Fact]
    public void MaintainedMarkdownHasValidLocalLinks()
    {
        // Arrange
        var missing = new List<string>();

        // Act
        foreach (var relative in Maintained)
        {
            var source = File.ReadAllText(Path.Combine(Root, relative));
            foreach (Match match in MarkdownLink().Matches(source))
            {
                var target = match.Groups[1].Value;
                if (target.StartsWith("http", StringComparison.OrdinalIgnoreCase) || target.StartsWith('#'))
                {
                    continue;
                }

                var path = target.Split('#')[0];
                if (!File.Exists(Path.GetFullPath(path, Path.GetDirectoryName(Path.Combine(Root, relative))!)))
                {
                    missing.Add($"{relative} links to missing file '{target}'.");
                }
            }
        }

        // Assert
        Assert.Empty(missing);
    }

    [Fact]
    public void AnalyzerManifestsListEveryCompilerDiagnostic()
    {
        // Arrange
        var declared = DiagnosticSourceDirectories
            .SelectMany(directory => Directory.EnumerateFiles(Path.Combine(Root, directory), "*.cs",
                SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            .SelectMany(path => DiagnosticId().Matches(File.ReadAllText(path)).Select(match => match.Value))
            .ToHashSet(StringComparer.Ordinal);

        // Act
        var documented = DiagnosticReleaseManifests
            .SelectMany(path => DiagnosticId().Matches(File.ReadAllText(Path.Combine(Root, path))))
            .Select(match => match.Value).ToHashSet(StringComparer.Ordinal);

        // Assert
        Assert.Subset(documented, declared);
    }

    [Fact]
    public void MaintainedDocumentationRejectsLegacyContracts()
    {
        // Arrange
        var paths = Maintained.Select(path => Path.Combine(Root, path)).ToArray();

        // Act
        var text = string.Join('\n', paths.Select(path => File.ReadAllText(path)));

        // Assert
        Assert.DoesNotContain(".AddWorker()", text, StringComparison.Ordinal);
        Assert.DoesNotContain("EventSchema.For", text, StringComparison.Ordinal);
        Assert.DoesNotContain("DeserializeRequest", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Fitz 0.1.2", text, StringComparison.Ordinal);
        Assert.DoesNotContain("AOT/trim analyzers are not enabled", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("reactors must dispatch", text, StringComparison.OrdinalIgnoreCase);

        // Portia.Fitz bundles FitzEventStore, FitzKvProjectionStore, and FitzKvCheckpointStore, so
        // documentation that still promises durable persistence as future work sends an application
        // to build an adapter it already has.
        Assert.DoesNotContain("until official adapters arrive", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("no database adapter is bundled today", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EveryProductProjectGeneratesXmlDocumentation()
    {
        // Arrange
        var projects = Directory.EnumerateDirectories(Path.Combine(Root, "src"), "Portia.*")
            .Where(path => !path.EndsWith(".Generators", StringComparison.Ordinal)
                           && !path.EndsWith("Portia.Analyzers", StringComparison.Ordinal)
                           && !path.EndsWith("Portia.CodeFixes", StringComparison.Ordinal))
            .ToArray();

        // Act
        var missing = projects
            .Select(project => (Project: project, Name: Path.GetFileName(project)))
            .Where(item => !File.Exists(Path.Combine(item.Project, "bin/Release/net11.0", item.Name + ".xml")))
            .Select(item => $"Generated XML documentation is missing for {item.Name}.")
            .ToArray();

        // Assert
        Assert.Empty(missing);
    }

    [Fact]
    public void GeneratedXmlContainsNewPublicSafetyContracts()
    {
        // Arrange
        (string Project, string[] Members)[] expected =
        [
            ("Portia.Abstractions", ["T:Cntryl.Portia.ReactionCommandFailedException"]),
            ("Portia.Fitz",
            [
                "T:Cntryl.Portia.FleetPartitionTerminationTimeoutException",
                "P:Cntryl.Portia.FleetRunOptions.PartitionStopTimeout"
            ]),
            ("Portia.Testing",
            [
                "T:Cntryl.Portia.Testing.ProjectionStoreConformance",
                "T:Cntryl.Portia.Testing.ReactionDeduplicationConformance",
                "T:Cntryl.Portia.Testing.ConformanceViolationException",
                "T:Cntryl.Portia.Testing.IWorkloadCoordinatorConformanceProbe",
                "T:Cntryl.Portia.Testing.IWorkloadCoordinatorConformanceWorker",
                "T:Cntryl.Portia.Testing.WorkloadCoordinatorConformance"
            ])
        ];

        // Act
        var documentation = expected.Select(item => (item.Members, Xml: File.ReadAllText(
            Path.Combine(Root, "src", item.Project, "bin/Release/net11.0", item.Project + ".xml")))).ToArray();

        // Assert
        foreach (var (members, xml) in documentation)
        {
            foreach (var member in members)
                Assert.Contains($"name=\"{member}\"", xml, StringComparison.Ordinal);
        }
    }

    static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Portia.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Could not locate the Portia repository root.");
    }

    [GeneratedRegex(@"\[[^\]]+\]\(([^)]+)\)")]
    private static partial Regex MarkdownLink();

    [GeneratedRegex(@"PORTIA\d{3}")]
    private static partial Regex DiagnosticId();
}
