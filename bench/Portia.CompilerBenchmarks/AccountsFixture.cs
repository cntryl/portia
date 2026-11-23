using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Cntryl.Portia.CompilerBenchmarks;

sealed class AccountsFixture
{
    static readonly ConcurrentDictionary<string, AccountsFixture> Cache = new(StringComparer.Ordinal);
    readonly CapturedProject _project;
    readonly CSharpCommandLineArguments _arguments;
    readonly ImmutableArray<MetadataReference> _references;
    readonly ImmutableArray<ISourceGenerator> _generators;
    readonly AnalyzerConfigOptionsProvider _options;
    readonly ImmutableArray<AdditionalText> _additional;
    public string Identity { get; }

    AccountsFixture(string directory, string component)
    {
        var manifestPath = Path.Combine(directory, "accounts.json");
        var text = File.ReadAllText(manifestPath);
        var manifest = AccountsCapture.Read(directory);
        _project = manifest.Projects.Single(project => project.Name.EndsWith("." + component, StringComparison.OrdinalIgnoreCase));
        Identity = CompilerHost.Hash(text + "|" + _project.Name);
        _arguments = CSharpCommandLineParser.Default.Parse(_project.Arguments, _project.Directory, sdkDirectory: null);
        _references = [.. _project.References.Select(reference => MetadataReference.CreateFromFile(Resolve(directory, reference.File),
            new MetadataReferenceProperties(MetadataImageKind.Assembly, [.. reference.Aliases], reference.EmbedInteropTypes)))];
        var generators = new List<ISourceGenerator>();

        foreach (var analyzer in _project.Analyzers)
        {
            var path = analyzer.Name switch
            {
                "Portia.Generators" => typeof(DomainEventCatalogGenerator).Assembly.Location,
                "Portia.AspNetCore.Generators" => typeof(RequestHttpBindingGenerator).Assembly.Location,
                _ => Resolve(directory, analyzer.File)
            };
            var reference = new AnalyzerFileReference(path, new SnapshotAssemblyLoader());
            reference.AnalyzerLoadFailed += (_, error) => throw new InvalidOperationException("Captured analyzer could not load: " + error.Message);
            generators.AddRange(reference.GetGenerators(LanguageNames.CSharp));
        }

        _generators = [.. generators];
        _options = new CapturedOptions(_project.Configurations);
        _additional = [.. _project.AdditionalFiles.Select(file => new CapturedAdditionalText(file))];
    }

    public static AccountsFixture Load(string component)
    {
        var path = Environment.GetEnvironmentVariable("PORTIA_COMPILER_ACCOUNTS")
            ?? throw new InvalidOperationException("Set PORTIA_COMPILER_ACCOUNTS to the directory created by capture-accounts.");
        var directory = Path.GetFullPath(path);
        return Cache.GetOrAdd(directory + "|" + component, _ => new AccountsFixture(directory, component));
    }

    static string Resolve(string directory, string relative) => AccountsCapture.Resolve(directory, relative);

    public CSharpCompilation Compilation() => CSharpCompilation.Create(_project.Name,
        _project.Sources.Select(source => CSharpSyntaxTree.ParseText(SourceText.From(source.Text, Encoding.UTF8),
            _arguments.ParseOptions, source.Path)), _references, _arguments.CompilationOptions);

    public GeneratorDriver Driver() => CSharpGeneratorDriver.Create(_generators, _additional,
        _arguments.ParseOptions, _options);

    public static CSharpCompilation Edit(CSharpCompilation compilation)
    {
        foreach (var tree in compilation.SyntaxTrees)
        {
            var block = tree.GetRoot().DescendantNodes().OfType<BlockSyntax>().FirstOrDefault();
            var topLevel = tree.GetRoot().DescendantNodes().OfType<GlobalStatementSyntax>().FirstOrDefault();

            if (block is null && topLevel is null)
                continue;

            var position = block is not null ? block.OpenBraceToken.Span.End : topLevel!.SpanStart;
            var source = tree.GetText();
            return compilation.ReplaceSyntaxTree(tree, tree.WithChangedText(source.WithChanges(
                new TextChange(new TextSpan(position, 0), "\n// compiler benchmark body edit\n"))));
        }

        throw new InvalidOperationException("Accounts project has no method/statement block to edit.");
    }

    sealed class SnapshotAssemblyLoader : IAnalyzerAssemblyLoader
    {
        // Frozen dependencies are adjacent to their entry DLL; LoadFrom provides sibling resolution.
        public void AddDependencyLocation(string fullPath) => ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        public Assembly LoadFromPath(string fullPath) => Assembly.LoadFrom(fullPath);
    }

    sealed class CapturedAdditionalText(CapturedText file) : AdditionalText
    {
        public override string Path => file.Path;
        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(file.Text, Encoding.UTF8);
    }

    sealed class CapturedOptions : AnalyzerConfigOptionsProvider
    {
        readonly AnalyzerConfigSet _set;
        public CapturedOptions(CapturedText[] files) => _set = AnalyzerConfigSet.Create(
            files.Select(file => AnalyzerConfig.Parse(SourceText.From(file.Text), file.Path)).ToArray());
        public override AnalyzerConfigOptions GlobalOptions => new Values(_set.GlobalConfigOptions.AnalyzerOptions);
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => new Values(_set.GetOptionsForSourcePath(tree.FilePath).AnalyzerOptions);
        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => new Values(_set.GetOptionsForSourcePath(textFile.Path).AnalyzerOptions);
    }

    sealed class Values(ImmutableDictionary<string, string> values) : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, out string value) => values.TryGetValue(key, out value!);
    }
}
