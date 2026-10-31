using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Cntryl.Portia.Consumer;

static class ApplicationAnalyzerCompilation
{
    public static ImmutableArray<Diagnostic> Analyze(string projectName, DiagnosticAnalyzer analyzer,
        (string Path, string Source)[] files, bool enabled = true, bool isTest = false,
        string? rootNamespace = null, string? projectDirectory = null, MetadataReference[]? references = null,
        string? assemblyName = null)
    {
        var platformReferences = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(path => !Path.GetFileName(path).StartsWith("Portia.", StringComparison.Ordinal)
                           && !Path.GetFileName(path).StartsWith("Cntryl.", StringComparison.Ordinal))
            .Append(typeof(Aggregate).Assembly.Location)
            .Concat(projectName.EndsWith(".Common", StringComparison.Ordinal) ? [] : new[] { typeof(PortiaBuilder).Assembly.Location })
            .Distinct(StringComparer.Ordinal)
            .Select(path => MetadataReference.CreateFromFile(path));
        var directory = projectDirectory ?? "/workspace/" + projectName;
        var compilation = CSharpCompilation.Create(assemblyName ?? projectName,
            files.Select(file => CSharpSyntaxTree.ParseText(file.Source,
                new CSharpParseOptions(LanguageVersion.Preview), file.Path.StartsWith('/')
                    ? file.Path : directory + "/" + file.Path)),
            platformReferences.Concat(references ?? []),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.DoesNotContain(compilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var diagnostics = compilation.WithAnalyzers([analyzer], Options(projectName, directory, rootNamespace, enabled, isTest))
            .GetAnalyzerDiagnosticsAsync().GetAwaiter().GetResult();
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "AD0001");
        return diagnostics;
    }

    public static AnalyzerOptions Options(string projectName, string directory, string? rootNamespace = null,
        bool enabled = true, bool isTest = false)
    {
        var values = new Dictionary<string, string>
        {
            ["build_property.PortiaProjectStructure"] = enabled ? "true" : "false",
            ["build_property.MSBuildProjectDirectory"] = directory,
            ["build_property.MSBuildProjectName"] = projectName,
            ["build_property.RootNamespace"] = rootNamespace ?? projectName,
            ["build_property.IsTestProject"] = isTest ? "true" : "false"
        }.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);
        return new AnalyzerOptions([], new ApplicationAnalyzerOptionsProvider(values));
    }
}
