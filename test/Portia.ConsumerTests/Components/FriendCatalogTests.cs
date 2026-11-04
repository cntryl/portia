using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Cntryl.Portia.Consumer;

/// <summary>Explicit event catalogs select the calling compilation even across friend assemblies.</summary>
public sealed class FriendCatalogTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void ShouldResolveLocalGeneratedCatalogGivenFriendAssembly(bool friend, bool sharedNamespace,
        bool referencesSharedEvent)
    {
        var identity = Guid.NewGuid().ToString("N");
        var sourceAssembly = "Catalog.Source." + identity;
        var consumerAssembly = "Catalog.Consumer." + identity;
        var declaration = friend ? "[assembly: InternalsVisibleTo(\"" + consumerAssembly + "\")]" : string.Empty;
        var first = Compile(sourceAssembly, $$"""
            using System.Runtime.CompilerServices;
            using Cntryl.Portia;
            {{declaration}}
            namespace Shared;
            [Discriminator("catalog.shared.changed", 1)] public sealed record SharedChanged : DomainEvent;
            public static class SourceComposition
            {
                public static DomainEventTypeCatalog Catalog(DomainEventTypeCatalog catalog)
                    => catalog.AddPortiaGeneratedDomainEvents();
            }
            """, []);
        var space = sharedNamespace ? "Shared" : "Consumer";
        var reference = referencesSharedEvent
            ? "public static System.Type ReferencedEvent => typeof(Shared.SharedChanged);" : string.Empty;
        var second = Compile(consumerAssembly, $$"""
            using Cntryl.Portia;
            namespace {{space}};
            [Discriminator("catalog.consumer.changed", 1)] public sealed record ConsumerChanged : DomainEvent;
            public static class ConsumerComposition
            {
                {{reference}}
                public static DomainEventTypeCatalog Catalog(DomainEventTypeCatalog catalog)
                    => catalog.AddPortiaGeneratedDomainEvents();
            }
            """, [MetadataReference.CreateFromImage(first.Image)]);
        var local = new DomainEventTypeCatalog();

        Register(second.Assembly, space + ".ConsumerComposition", local);

        Assert.True(local.TryResolve("catalog.consumer.changed", 1, out var consumer));
        Assert.Equal(consumerAssembly, consumer!.Assembly.GetName().Name);
        Assert.False(local.TryResolve("catalog.shared.changed", 1, out _));
        // Explicit catalogs remain per assembly: composing them must not double-register a
        // referenced event merely because the friend consumer names its type.
        Register(first.Assembly, "Shared.SourceComposition", local);
        Assert.True(local.TryResolve("catalog.shared.changed", 1, out var shared));
        Assert.Equal(sourceAssembly, shared!.Assembly.GetName().Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShouldRegisterEmptyCatalogGivenConditionalCall(bool create)
    {
        var assembly = GeneratorCompilation.Compile("""
            #nullable enable
            using Cntryl.Portia;
            public static class Scenario
            {
                public static DomainEventTypeCatalog? Run(bool create)
                {
                    DomainEventTypeCatalog? catalog = create ? new() : null;
                    return catalog?.AddPortiaGeneratedDomainEvents();
                }
            }
            """, new DomainEventCatalogGenerator());

        var result = assembly.GetType("Scenario")!.GetMethod("Run")!.Invoke(null, [create]);

        Assert.Equal(create, result is DomainEventTypeCatalog);
    }

    [Fact]
    public void ShouldPreserveCompiledLegacyCatalogGivenNewFriendConsumer()
    {
        var identity = Guid.NewGuid().ToString("N");
        var consumerAssembly = "Catalog.Legacy.Consumer." + identity;
        // This is the extension shape emitted by earlier generators. Its explicit static call
        // preserves the compiled legacy call even though the framework now owns an instance entry.
        var first = Compile("Catalog.Legacy.Source." + identity, $$"""
            using System.Runtime.CompilerServices;
            using Cntryl.Portia;
            [assembly: InternalsVisibleTo("{{consumerAssembly}}")]
            namespace Shared
            {
                [Discriminator("catalog.legacy.changed", 1)] public sealed record LegacyChanged : DomainEvent;
                public static class LegacyComposition
                {
                    public static DomainEventTypeCatalog Catalog(DomainEventTypeCatalog catalog)
                        => DomainEventTypeCatalogGeneratedExtensions.AddPortiaGeneratedDomainEvents(catalog);
                }
            }
            namespace Cntryl.Portia
            {
                internal static class DomainEventTypeCatalogGeneratedExtensions
                {
                    public static DomainEventTypeCatalog AddPortiaGeneratedDomainEvents(this DomainEventTypeCatalog catalog)
                        => catalog.Register<Shared.LegacyChanged>(1, "catalog.legacy.changed");
                }
            }
            """, []);
        var second = Compile(consumerAssembly, """
            using Cntryl.Portia;
            namespace Consumer;
            [Discriminator("catalog.current.changed", 1)] public sealed record CurrentChanged : DomainEvent;
            public static class ConsumerComposition
            {
                public static DomainEventTypeCatalog Catalog(DomainEventTypeCatalog catalog)
                    => catalog.AddPortiaGeneratedDomainEvents();
            }
            """, [MetadataReference.CreateFromImage(first.Image)]);
        var catalog = new DomainEventTypeCatalog();

        Register(first.Assembly, "Shared.LegacyComposition", catalog);
        Register(second.Assembly, "Consumer.ConsumerComposition", catalog);

        Assert.True(catalog.TryResolve("catalog.legacy.changed", 1, out _));
        Assert.True(catalog.TryResolve("catalog.current.changed", 1, out var current));
        Assert.Equal(consumerAssembly, current!.Assembly.GetName().Name);
    }

    [Fact]
    public void ShouldDiagnoseCatalogMethodGroupBeforeExecution()
    {
        var diagnostics = GeneratorCompilation.Diagnostics("""
            using System;
            using Cntryl.Portia;
            public static class Scenario
            {
                public static Func<DomainEventTypeCatalog> Bind(DomainEventTypeCatalog catalog)
                    => catalog.AddPortiaGeneratedDomainEvents;
            }
            """, new DomainEventCatalogGenerator());

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "PORTIA019");
    }

    [Theory]
    [InlineData("DomainEventTypeCatalog.AddPortiaGeneratedDomainEvents")]
    [InlineData("catalog.AddPortiaGeneratedDomainEvents")]
    public void ShouldAllowCatalogNameofWithoutRegistration(string member)
    {
        var diagnostics = GeneratorCompilation.OutputDiagnostics($$"""
            using Cntryl.Portia;
            public static class Scenario
            {
                public static string Name(DomainEventTypeCatalog catalog) => nameof({{member}});
            }
            """, new DomainEventCatalogGenerator());

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void ShouldExplainMissingGeneratorGivenCatalogEntryPoint()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            new DomainEventTypeCatalog().AddPortiaGeneratedDomainEvents());

        Assert.Contains("did not intercept", error.Message, StringComparison.Ordinal);
        Assert.Contains("analyzer assets", error.Message, StringComparison.Ordinal);
    }

    static void Register(Assembly assembly, string composition, DomainEventTypeCatalog catalog) =>
        assembly.GetType(composition)!.GetMethod("Catalog")!.Invoke(null, [catalog]);

    static (Assembly Assembly, byte[] Image) Compile(string assemblyName, string source,
        MetadataReference[] additionalReferences)
    {
        var options = new CSharpParseOptions(LanguageVersion.Preview).WithFeatures(
            [new KeyValuePair<string, string>("InterceptorsNamespaces", "Cntryl.Portia.Generated")]);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(Aggregate).Assembly.Location).Distinct(StringComparer.Ordinal)
            .Select(path => MetadataReference.CreateFromFile(path)).Concat(additionalReferences);
        var compilation = CSharpCompilation.Create(assemblyName,
            [CSharpSyntaxTree.ParseText(source, options, "Catalog.cs")], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                generalDiagnosticOption: ReportDiagnostic.Error));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new DomainEventCatalogGenerator().AsSourceGenerator()], parseOptions: options);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
        Assert.All(driver.GetRunResult().Results, result => Assert.Null(result.Exception));
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        using var image = new MemoryStream();
        var emitted = output.Emit(image);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        var bytes = image.ToArray();
        image.Position = 0;
        return (AssemblyLoadContext.Default.LoadFromStream(image), bytes);
    }
}
