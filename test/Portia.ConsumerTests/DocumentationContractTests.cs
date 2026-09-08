using System.Text.RegularExpressions;

namespace Cntryl.Portia.Consumer;

public sealed partial class DocumentationContractTests
{
    static readonly string Root = FindRoot();
    static readonly string[] Maintained =
    [
        "README.md",
        "docs/getting-started.md",
        "docs/application-setup.md",
        "docs/projectors-and-reactors.md",
        "docs/request-context.md",
        "docs/observability.md",
        "docs/native-aot.md",
        "docs/scope.md",
        "docs/design-decisions.md",
    ];

    [Fact]
    public void MaintainedMarkdownHasValidLocalLinks()
    {
        foreach (var relative in Maintained)
        {
            var source = File.ReadAllText(Path.Combine(Root, relative));
            foreach (Match match in MarkdownLink().Matches(source))
            {
                var target = match.Groups[1].Value;
                if (target.StartsWith("http", StringComparison.OrdinalIgnoreCase) || target.StartsWith('#'))
                    continue;
                var path = target.Split('#')[0];
                Assert.True(File.Exists(Path.GetFullPath(path, Path.GetDirectoryName(Path.Combine(Root, relative))!)),
                    $"{relative} links to missing file '{target}'.");
            }
        }
    }

    [Fact]
    public void AnalyzerManifestListsEveryGeneratorDiagnostic()
    {
        var declared = Directory.EnumerateFiles(Path.Combine(Root, "src/Portia.Generators"), "*.cs")
            .SelectMany(path => DiagnosticId().Matches(File.ReadAllText(path)).Select(match => match.Value))
            .ToHashSet(StringComparer.Ordinal);
        var documented = DiagnosticId().Matches(File.ReadAllText(Path.Combine(Root, "docs/AnalyzerReleases.Unshipped.md")))
            .Select(match => match.Value).ToHashSet(StringComparer.Ordinal);
        Assert.Subset(documented, declared);
    }

    [Fact]
    public void MaintainedDocumentationRejectsLegacyContractsAndProductXmlExists()
    {
        var text = string.Join('\n', Maintained.Select(path => File.ReadAllText(Path.Combine(Root, path))));
        Assert.DoesNotContain(".AddWorker()", text, StringComparison.Ordinal);
        Assert.DoesNotContain("EventSchema.For", text, StringComparison.Ordinal);
        Assert.DoesNotContain("DeserializeRequest", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Fitz 0.1.2", text, StringComparison.Ordinal);
        Assert.DoesNotContain("AOT/trim analyzers are not enabled", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("reactors must dispatch", text, StringComparison.OrdinalIgnoreCase);
        foreach (var project in Directory.EnumerateDirectories(Path.Combine(Root, "src"), "Portia.*")
            .Where(path => !path.EndsWith("Portia.Generators", StringComparison.Ordinal)
                && !path.EndsWith("Portia.CodeFixes", StringComparison.Ordinal)))
        {
            var name = Path.GetFileName(project);
            Assert.True(File.Exists(Path.Combine(project, "bin/Release/net10.0", name + ".xml")),
                $"Generated XML documentation is missing for {name}.");
        }
    }

    [Fact]
    public void GeneratedXmlContainsNewPublicSafetyContracts()
    {
        AssertXmlMembers("Portia.Abstractions",
            "T:Cntryl.Portia.ReactionCommandFailedException");
        AssertXmlMembers("Portia.Fitz",
            "T:Cntryl.Portia.FleetPartitionTerminationTimeoutException",
            "P:Cntryl.Portia.FleetRunOptions.PartitionStopTimeout");
        AssertXmlMembers("Portia.Testing",
            "T:Cntryl.Portia.FencingTokenConformance",
            "T:Cntryl.Portia.ProjectionStoreConformance",
            "T:Cntryl.Portia.ReactionDeduplicationConformance",
            "T:Cntryl.Portia.ConformanceViolationException");
    }

    static void AssertXmlMembers(string project, params string[] expected)
    {
        var xml = File.ReadAllText(Path.Combine(Root, "src", project, "bin/Release/net10.0", project + ".xml"));
        foreach (var member in expected)
            Assert.Contains($"name=\"{member}\"", xml, StringComparison.Ordinal);
    }

    static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Portia.slnx")))
                return directory.FullName;
        }
        throw new InvalidOperationException("Could not locate the Portia repository root.");
    }

    [GeneratedRegex(@"\[[^\]]+\]\(([^)]+)\)")]
    private static partial Regex MarkdownLink();

    [GeneratedRegex(@"PORTIA\d{3}")]
    private static partial Regex DiagnosticId();
}
